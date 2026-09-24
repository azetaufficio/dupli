# Deploy del management server su Azure Container Apps + primo agent Windows

Guida passo passo per:
1. pubblicare il management server Dupli (API + web UI) su **Azure Container Apps (ACA)** con URL pubblico HTTPS;
2. preparare lo storage S3 (Wasabi o Backblaze B2);
3. installare e verificare il primo **agent** su una VM Windows.

I comandi usano bash con Azure CLI (`az`). Da Windows si può usare Azure Cloud Shell (bash) oppure WSL.

---

## 0. Architettura su Azure

```
Browser operatori ──HTTPS──┐
                           ▼
Agent Windows ──HTTPS──► Azure Container Apps (1 replica, ingress esterno, TLS gestito)
                           │  container dupli-server (porta 8080)
                           │   ├─ /var/lib/dupli/tools ← effimero (cache mirror restic + restic del server)
                           │   └─ /var/lib/dupli/cache ← effimero (cache restic per il browse degli snapshot)
                           ├──► Azure Key Vault (key ring Data Protection, managed identity)
                           ▼
              Azure Database for PostgreSQL – Flexible Server (DB "dupli")

Agent Windows ──HTTPS──► S3 (Wasabi / B2): 1 repository restic per VM
Server ─────────HTTPS──► S3 (sola lettura, --no-lock): lista e browse degli snapshot dalla UI
```

Vincoli da rispettare (dipendono da com'è fatto il server oggi):

| Vincolo | Motivo |
|---|---|
| **Esattamente 1 replica** (`minReplicas = maxReplicas = 1`) | Scheduler, sweeper e alert girano dentro il processo: con 2 repliche si avrebbero notifiche doppie e, senza `SigningKey` configurata, token agent non validi tra repliche. Con 0 repliche lo scheduler non gira. |
| **Key ring Data Protection in Key Vault, separato dal DB** | Cifra password dei repository e chiavi S3 in escrow. Se si perde il key ring, i segreti in DB non sono più decifrabili. In Key Vault è cifrato a riposo, accessibile solo alla managed identity dell'app, e protetto da soft delete + purge protection. |
| **Scegliere l'URL definitivo prima di installare agent** | L'URL del server viene salvato in `agent.json` all'enrollment. Se il dominio cambia dopo, gli agent vanno ri-enrollati (o `agent.json` modificato a mano). |
| **HTTPS obbligatorio** | Il login OIDC con Entra ID richiede redirect URI `https://`. ACA termina il TLS e passa `X-Forwarded-Proto`, che il server usa. |

Costo indicativo (verificare con il calcolatore Azure): ACA consumption con 1 replica sempre attiva da 0,5 vCPU / 1 GiB, più PostgreSQL Flexible Burstable B1ms, più pochi centesimi di Key Vault e Log Analytics.

---

## 1. Prerequisiti

- Sottoscrizione Azure con permessi di Contributor sul resource group, e permessi per creare **app registration** in Entra ID (o un amministratore che le crei).
- Azure CLI aggiornata:
  ```bash
  az login
  az extension add --name containerapp --upgrade
  az provider register --namespace Microsoft.App
  az provider register --namespace Microsoft.OperationalInsights
  az provider register --namespace Microsoft.DBforPostgreSQL
  az provider register --namespace Microsoft.KeyVault
  az provider register --namespace Microsoft.ManagedIdentity
  ```
- `openssl` ed `envsubst` (pacchetto `gettext`) sulla macchina da cui lanci i comandi (in Cloud Shell ci sono già).
- Il codice M3 **committato e pushato su `main`**: la CI (job `docker`) pubblica l'immagine `ghcr.io/azetaufficio/dupli-server:sha-<commit>` e l'exe dell'agent come artifact `dupli-agent-win-x64`.

Variabili usate in tutta la guida (adattale):

```bash
RG=dupli-rg
LOC=italynorth                 # oppure westeurope
ENV_NAME=dupli-env
APP=dupli-server
PG=dupli-pg-$RANDOM            # nome globale univoco
KV=dupli-kv-$RANDOM            # 3-24 caratteri, nome globale univoco
IDENTITY=dupli-server-id
PG_ADMIN=dupliadmin
PG_PASS="$(openssl rand -base64 24 | tr -d '/+=')"
IMAGE=ghcr.io/azetaufficio/dupli-server:sha-<commit>   # tag immutabile, NON latest
echo "Password PostgreSQL: $PG_PASS"                   # salvala nel password manager
```

---

## 2. Immagine del container

### Opzione A — GHCR (quella predefinita, la CI la pubblica già)
1. Fai push su `main` e aspetta che la CI sia verde.
2. In GitHub apri **organizzazione → Packages → dupli-server**.
3. Se il pacchetto è privato, hai due strade:
   - renderlo pubblico: **Package settings → Change visibility → Public** (il codice è già pubblico, quindi va bene);
   - oppure lasciarlo privato e passare ad ACA le credenziali di registry, con un PAT che abbia `read:packages`. Aggiungi `--registry-server ghcr.io --registry-username <utente> --registry-password <PAT>` quando crei l'app, oppure la sezione `registries` nel YAML.
4. Prendi il tag `sha-<commit>` dalla pagina del pacchetto e mettilo in `IMAGE`.

### Opzione B — Azure Container Registry (se non vuoi dipendere da GHCR)
```bash
ACR=dupliacr$RANDOM
az acr create -g $RG -n $ACR --sku Basic
az acr build -r $ACR -t dupli-server:$(git rev-parse --short HEAD) .   # dalla root del repo
IMAGE=$ACR.azurecr.io/dupli-server:$(git rev-parse --short HEAD)
```
Poi crea l'app con identità gestita e pull da ACR (`az containerapp registry set --server $ACR.azurecr.io --identity system`).

---

## 3. Resource group e PostgreSQL

```bash
az group create -n $RG -l $LOC

az postgres flexible-server create -g $RG -n $PG -l $LOC \
  --tier Burstable --sku-name Standard_B1ms --storage-size 32 --version 18 \
  --admin-user $PG_ADMIN --admin-password "$PG_PASS" \
  --public-access 0.0.0.0 --yes

az postgres flexible-server db create -g $RG -s $PG -d dupli
```

- `--public-access 0.0.0.0` crea la regola firewall "consenti servizi Azure": è la configurazione più semplice. Il DB resta protetto da password e TLS, ma la regola vale per qualunque servizio Azure, anche di altri tenant. Per chiudere di più servono VNet integration dell'ambiente ACA e accesso privato al DB (da valutare dopo, vedi HANDOFF).
- Se `--version 18` non è disponibile nella region, usa `17` (pg_dump 18 dell'agent la supporta).
- Backup automatici: di default 7 giorni di retention. Il DB contiene i segreti in escrow, cifrati con il key ring del punto 4.

Connection string (TLS con verifica del certificato):
```bash
DB_CONNECTION="Host=$PG.postgres.database.azure.com;Database=dupli;Username=$PG_ADMIN;Password=$PG_PASS;SSL Mode=VerifyFull"
```
Se `VerifyFull` fallisce per un problema di catena di certificati, ripiega su `SSL Mode=Require` e annotalo.

---

## 4. Key Vault per il key ring + managed identity

Il key ring Data Protection (le chiavi che cifrano password dei repository e chiavi S3 in escrow, e i cookie di sessione) viene salvato come secret del Key Vault, un secret per chiave (`dupli-dataprotection-key-<guid>`). L'app accede con una managed identity **user-assigned**, creata prima dell'app, così il permesso c'è già al primo avvio: il server carica il key ring all'avvio e, se non ci riesce, esce con `Fatal:`.

```bash
az keyvault create -g $RG -n $KV -l $LOC   --enable-rbac-authorization true --enable-purge-protection true --retention-days 90

az identity create -g $RG -n $IDENTITY
IDENTITY_ID=$(az identity show -g $RG -n $IDENTITY --query id -o tsv)
IDENTITY_CLIENT_ID=$(az identity show -g $RG -n $IDENTITY --query clientId -o tsv)
IDENTITY_PRINCIPAL_ID=$(az identity show -g $RG -n $IDENTITY --query principalId -o tsv)

KV_ID=$(az keyvault show -g $RG -n $KV --query id -o tsv)
az role assignment create --assignee-object-id $IDENTITY_PRINCIPAL_ID --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets Officer" --scope $KV_ID
```

Il ruolo serve per leggere, elencare e scrivere secret. Data Protection non cancella mai chiavi, quindi l'app non ha bisogno di altro. Con purge protection attiva, un secret cancellato per errore resta recuperabile per 90 giorni. Metti comunque un **resource lock** (`CanNotDelete`) sul vault.

> Alternativa senza Key Vault: `DataProtection__KeyStore=FileSystem` con una share Azure Files montata su `/var/lib/dupli/keys` (è il default in compose). Le chiavi però restano in chiaro sulla share: chi legge la share può decifrare i segreti in escrow.

---

## 5. Ambiente Container Apps

```bash
az containerapp env create -g $RG -n $ENV_NAME -l $LOC

ENV_ID=$(az containerapp env show -g $RG -n $ENV_NAME --query id -o tsv)
DOMAIN=$(az containerapp env show -g $RG -n $ENV_NAME --query properties.defaultDomain -o tsv)
FQDN=$APP.$DOMAIN
echo "URL pubblico: https://$FQDN"
```

L'URL `https://<app>.<dominio-ambiente>` si conosce già adesso, prima di creare l'app: serve per l'app registration.

> **Dominio personalizzato** (es. `backup.tuodominio.it`): decidilo ora. Se lo usi, metti in `FQDN` il dominio custom per Entra e per `Dupli__PublicUrl`, e configuralo al punto 9 **prima** di installare agent.

---

## 6. App registration Entra ID (login alla web UI)

### Da portale (consigliato la prima volta)
1. **Entra ID → App registrations → New registration**
   - Name: `Dupli`
   - Supported account types: *Accounts in this organizational directory only*
   - Redirect URI: piattaforma **Web**, `https://<FQDN>/signin-oidc`
2. **Authentication**:
   - aggiungi un secondo redirect URI Web: `https://<FQDN>/signout-callback-oidc`. Serve per tornare all'app dopo il logout.
   - *Front-channel logout URL*: `https://<FQDN>/signout-callback-oidc`.
   - Lascia disattivati *Access tokens* e *ID tokens* (implicit flow): il server usa authorization code + PKCE.
3. **Certificates & secrets → New client secret**: copia il *Value*, che diventa `ENTRA_CLIENT_SECRET`. Segna la scadenza e mettiti un promemoria per rinnovarlo.
4. **Overview**: copia *Application (client) ID* (`ENTRA_CLIENT_ID`) e *Directory (tenant) ID* (`ENTRA_TENANT_ID`).
5. Chi può entrare, e con quale ruolo (`Owner`, `Operator`, `Viewer`), si decide in Dupli, dalla pagina **Users**. Al primo avvio la tabella utenti è vuota: entra solo l'email indicata in `DUPLI_BOOTSTRAP_OWNER_EMAIL`, che diventa `Owner`. Gli altri vanno invitati per email; al primo accesso l'invito viene legato all'account Entra (`oid`). Facoltativo: Enterprise applications → Dupli → Properties → *Assignment required* = **Yes**, per bloccare già in Entra chi non è assegnato.

### Da CLI (alternativa)
```bash
ENTRA_TENANT_ID=$(az account show --query tenantId -o tsv)
ENTRA_CLIENT_ID=$(az ad app create --display-name Dupli --sign-in-audience AzureADMyOrg \
  --web-redirect-uris "https://$FQDN/signin-oidc" "https://$FQDN/signout-callback-oidc" \
  --query appId -o tsv)
az ad sp create --id $ENTRA_CLIENT_ID
ENTRA_CLIENT_SECRET=$(az ad app credential reset --id $ENTRA_CLIENT_ID --display-name dupli-server --years 1 --query password -o tsv)
```
Il passo 5 (facoltativo: *Assignment required*) si fa dal portale.

---

## 7. Notifiche email (alert)

Scegli un canale e prepara le variabili:

- **Office 365 (Microsoft Graph):** crea una **seconda** app registration (es. `Dupli Mailer`, separata da quella di login). Dalle il permesso *Application* `Mail.Send` con admin consent, e crea un client secret. Limitala alla sola casella mittente con una *application access policy* di Exchange Online (o RBAC for Applications): senza limite può inviare come qualunque utente. Variabili: `Notifications__Channel=Office365`, più `Notifications__Office365__TenantId`, `ClientId`, `ClientSecret`, `From` e `To__0`.
- **SMTP:** `Notifications__Channel=Smtp`, più `Notifications__Smtp__Host`, `Port`, `Security`, `Username`, `Password`, `From` e `To__0`.

Il template del punto 8 usa Office 365. Per SMTP sostituisci il blocco env relativo.

---

## 8. Creazione della Container App

Genera la chiave di firma dei token agent. È opzionale, ma se la fissi un riavvio del server non costringe gli agent a riautenticarsi:
```bash
SIGNING_KEY=$(openssl rand -base64 32)
export LOC ENV_ID IMAGE FQDN KV IDENTITY_ID IDENTITY_CLIENT_ID DB_CONNECTION SIGNING_KEY ENTRA_TENANT_ID ENTRA_CLIENT_ID ENTRA_CLIENT_SECRET
export DUPLI_BOOTSTRAP_OWNER_EMAIL=tu@tuodominio.it   # primo Owner, vedi punto 6.5
export O365_TENANT_ID=... O365_CLIENT_ID=... O365_CLIENT_SECRET=... O365_FROM=dupli@tuodominio.it O365_TO=ops@tuodominio.it
```

Il template è nel repo: `deploy/azure/containerapp.template.yaml`, riportato qui sotto. Non contiene segreti: i valori arrivano da `envsubst`.

```yaml
location: ${LOC}
identity:
  # Reads and writes the Data Protection key ring in Key Vault (role Key Vault Secrets Officer on the vault).
  type: UserAssigned
  userAssignedIdentities:
    ${IDENTITY_ID}: {}
properties:
  managedEnvironmentId: ${ENV_ID}
  configuration:
    activeRevisionsMode: Single
    ingress:
      external: true
      targetPort: 8080
      transport: auto
      allowInsecure: false
    secrets:
      - name: db-connection
        value: "${DB_CONNECTION}"
      - name: agents-signing-key
        value: "${SIGNING_KEY}"
      - name: entra-client-secret
        value: "${ENTRA_CLIENT_SECRET}"
      - name: o365-client-secret
        value: "${O365_CLIENT_SECRET}"
  template:
    containers:
      - name: dupli-server
        image: ${IMAGE}
        resources:
          cpu: 0.5
          memory: 1Gi
        env:
          - name: ConnectionStrings__Dupli
            secretRef: db-connection
          - name: Dupli__PublicUrl
            value: https://${FQDN}
          - name: Dupli__Agents__SigningKey
            secretRef: agents-signing-key
          - name: DataProtection__KeyStore
            value: AzureKeyVault
          - name: DataProtection__AzureKeyVault__VaultUri
            value: https://${KV}.vault.azure.net/
          - name: DataProtection__AzureKeyVault__Credential
            value: ManagedIdentity
          - name: DataProtection__AzureKeyVault__ClientId
            value: ${IDENTITY_CLIENT_ID}
          - name: Dupli__Auth__Mode
            value: EntraId
          - name: Dupli__Auth__EntraId__TenantId
            value: ${ENTRA_TENANT_ID}
          - name: Dupli__Auth__EntraId__ClientId
            value: ${ENTRA_CLIENT_ID}
          - name: Dupli__Auth__EntraId__ClientSecret
            secretRef: entra-client-secret
          - name: Dupli__Auth__BootstrapOwnerEmail
            value: ${DUPLI_BOOTSTRAP_OWNER_EMAIL}
          - name: Notifications__Channel
            value: Office365
          - name: Notifications__Office365__TenantId
            value: ${O365_TENANT_ID}
          - name: Notifications__Office365__ClientId
            value: ${O365_CLIENT_ID}
          - name: Notifications__Office365__ClientSecret
            secretRef: o365-client-secret
          - name: Notifications__Office365__From
            value: ${O365_FROM}
          - name: Notifications__Office365__To__0
            value: ${O365_TO}
        probes:
          - type: Liveness
            httpGet:
              path: /health
              port: 8080
            initialDelaySeconds: 15
            periodSeconds: 30
          - type: Readiness
            httpGet:
              path: /health
              port: 8080
            initialDelaySeconds: 5
            periodSeconds: 10
    scale:
      minReplicas: 1
      maxReplicas: 1
```

Il template non imposta `Dupli__Admin__ApiKey`, quindi la chiave admin resta disattivata: in produzione si usa solo il login Entra. Se ti serve per degli script, aggiungila come secret.

**Recupero accessi (break-glass):** se nessun Owner riesce più a entrare (account disabilitato, persona uscita dall'azienda), imposta temporaneamente `Dupli__Admin__ApiKey`, riavvia e apri `https://<FQDN>/admin`. Con la chiave si apre una sessione di 15 minuti che permette solo di gestire gli utenti (invitare un nuovo Owner, riabilitare o cancellare utenti). Poi togli di nuovo la chiave.

Crea l'app senza lasciare file con segreti su disco:
```bash
envsubst < deploy/azure/containerapp.template.yaml > /tmp/dupli-app.yaml
az containerapp create -g $RG -n $APP --yaml /tmp/dupli-app.yaml
rm /tmp/dupli-app.yaml
```

### Verifica del deploy
```bash
curl -s https://$FQDN/health                    # {"status":"ok"}
az containerapp logs show -g $RG -n $APP --follow --tail 100
```
Nei log del primo avvio devi vedere:
- `Applied database script ...0001_initial.sql` e `...0002_m3.sql` (le migrazioni DbUp sul DB Azure);
- **nessun** errore Data Protection o Key Vault (`403 Forbidden` = ruolo mancante sulla managed identity, vedi punto 4);
- nessun `Fatal:`. In quel caso il container esce e ACA lo riavvia: leggi il messaggio, di solito è configurazione mancante.

Poi dal browser:
1. `https://<FQDN>` ti porta al login Microsoft e poi alla dashboard vuota.
2. In alto a destra compare il tuo nome; *Sign out* ti riporta al login.
3. Un account non invitato in Dupli vede la pagina *Access denied* (o viene bloccato già da Entra se hai attivato *Assignment required*).

Dopo il primo avvio controlla che il vault contenga il key ring:
```bash
az keyvault secret list --vault-name $KV --query "[].name" -o tsv   # dupli-dataprotection-key-<guid>
```
Per leggere i secret dal tuo utente serve un ruolo sul vault (es. *Key Vault Secrets User*): la sola appartenenza al resource group non basta.

---

## 9. Dominio personalizzato (opzionale, prima degli agent)

```bash
az containerapp hostname add -g $RG -n $APP --hostname backup.tuodominio.it
# crea i record DNS indicati (CNAME + TXT asuid), poi:
az containerapp hostname bind -g $RG -n $APP --hostname backup.tuodominio.it \
  --environment $ENV_NAME --validation-method CNAME
```
Poi:
- aggiorna i redirect URI dell'app registration con il nuovo host;
- aggiorna `Dupli__PublicUrl`: `az containerapp update -g $RG -n $APP --set-env-vars Dupli__PublicUrl=https://backup.tuodominio.it`.

---

## 10. Storage S3 per i backup

Una volta per provider:
1. Crea il bucket, per esempio `dupli-backups`, nella region più vicina alle VM.
2. Attiva il **versioning** sul bucket.
3. Aggiungi una **lifecycle rule** che elimina le versioni non correnti dopo 30 giorni.
4. Endpoint da inserire nello *Storage target* della UI:
   - Wasabi: `https://s3.<region>.wasabisys.com` (es. `https://s3.eu-south-1.wasabisys.com`, region `eu-south-1`);
   - Backblaze B2: `https://s3.<region>.backblazeb2.com` (es. `https://s3.eu-central-003.backblazeb2.com`, region `eu-central-003`).

Per ogni VM:
1. Crea una **chiave S3 dedicata**, limitata al bucket e al prefisso della VM (es. `agents/vm-01/`):
   - Wasabi: sub-user con una policy IAM su `arn:aws:s3:::dupli-backups/agents/vm-01/*`, più `ListBucket` con condizione sul prefisso;
   - B2: application key limitata al bucket, con *File name prefix* = `agents/vm-01/`.
2. Obiettivo anti-ransomware: la chiave dell'agent **non** deve poter cancellare definitivamente le versioni (niente `DeleteObjectVersion` / permessi di bypass). Verifica con il provider cosa comporta per `restic prune`: vedi HANDOFF.
3. La stessa chiave la usa anche il server (in sola lettura) per mostrare snapshot e file nella tab *Snapshots*: l'egress del Container App verso l'endpoint S3 deve essere aperto. Se il server raggiunge S3 da un URL diverso da quello degli agent (endpoint privato), configura `Dupli__Restore__EndpointOverrides__0__From` / `__To`.

---

## 11. Registrare la VM nel server (web UI)

1. **Storage → New storage target**: nome, endpoint, bucket e region del punto 10.
2. **Agents → New agent**:
   - Name: `vm-01`
   - Storage target: quello appena creato
   - Storage prefix: `agents/vm-01` (uguale al prefisso della chiave S3)
   - S3 access key / secret key: quelli della chiave dedicata
   - Repository password: **vuoto**, così la genera il server e la conserva in escrow. Compila solo per riusare un repository M1 esistente.
3. Nel dettaglio dell'agent: **Generate enrollment token**. Il token è monouso, vale 24 h e viene mostrato una sola volta. Copia il comando di installazione che compare.

---

## 12. Installare l'agent sulla VM Windows

Prerequisiti della VM:
- Windows Server 2016+ / Windows 10+ x64, con orologio sincronizzato (NTP);
- uscita HTTPS (443) verso `https://<FQDN>` e verso l'endpoint S3 (non servono porte in ingresso);
- se fai backup di PostgreSQL: PostgreSQL installato con l'installer standard, così `pg_dump` viene trovato dal registro; altrimenti imposti *Bin directory* nella policy.

Procedura:
1. Scarica l'exe dalla release GitHub (`dupli-agent_<ver>_windows_amd64.exe`, con il suo `.sha256`), oppure dall'artifact **dupli-agent-win-x64** dell'ultima run verde su `main`. Copialo sulla VM come `C:\Temp\dupli-agent.exe`.
2. Apri **PowerShell come Amministratore**:
   ```powershell
   cd C:\Temp
   Unblock-File .\dupli-agent.exe          # exe non ancora firmato: evita il blocco "scaricato da internet"
   .\dupli-agent.exe install --server https://<FQDN> --token <TOKEN>
   sc.exe start DupliAgent
   ```
   L'install fa l'enrollment e salva segreti, password del repository e chiave S3 con DPAPI. Poi copia l'exe in `C:\ProgramData\Dupli\versions\<ver>\` e come Launcher in `C:\Program Files\Dupli\Launcher\`, restringe l'ACL di `C:\ProgramData\Dupli` (SYSTEM e Administrators, Users in sola lettura) e registra il servizio `DupliAgent` (LocalSystem, avvio automatico, riavvio automatico in caso di crash) che esegue `dupli-agent.exe launch`. **Non avvia il servizio**: per quello serve `sc.exe start`.
   Se Defender o SmartScreen bloccano l'exe (non firmato), aggiungi un'eccezione per quel file. Con un certificato di firma: aggiungi `--require-signature --signer-thumbprint <thumbprint>` e l'agent accetterà solo aggiornamenti firmati da quel certificato.
3. Se farai backup PostgreSQL, salva la password del DB con lo stesso nome che userai nella policy (campo *Password secret*, es. `pg-main`):
   ```powershell
   $exe = "C:\Program Files\Dupli\Launcher\dupli-agent.exe"
   $p = Read-Host "Password PostgreSQL" -AsSecureString
   [Net.NetworkCredential]::new('', $p).Password | & $exe secret set pg-main
   ```
4. Entro circa 30 secondi, nella UI l'agent passa a **Active / online** con hostname, OS, versione e spazio disco. Nella tab *Logs* compaiono i log caricati dall'agent.
   Log locali: `C:\ProgramData\Dupli\logs\` (`agent-*.json` e `launcher-*.json`). Stato del servizio: `sc.exe query DupliAgent`.

---

## 13. Prima policy e verifiche end-to-end

1. Agent → **New policy**:
   - cartelle, per esempio `D:\Dati` (una per riga), con eventuali esclusioni;
   - PostgreSQL: host `localhost`, porta, utente, *Password secret* `pg-main`; database: *tutti tranne gli esclusi* (template e `postgres` sempre esclusi) oppure *solo quelli elencati* (anche `postgres`; un DB elencato che non esiste fa fallire il backup);
   - cron, per esempio `0 2 * * *` con fuso `Europe/Rome`: l'anteprima mostra le prossime esecuzioni;
   - retention daily/weekly/monthly.
2. **Run now** sulla policy. Nella tab *Jobs* lo stato passa Pending → Running → Succeeded; nella tab *Backup history* compaiono snapshot e byte.
3. **Run restore test**: nella tab *Restore tests* ogni file del campione e ogni DB devono risultare Succeeded.
4. **Check repository**: Succeeded.
5. Restore dalla UI: tab *Snapshots* → *Browse* su uno snapshot di cartelle, seleziona file o cartelle (oppure niente = tutto) → *Restore*. Il job va sull'agent e scrive in `C:\DupliRestore\<job-id>` (o in una cartella a scelta, nuova o vuota, fuori dai percorsi sotto backup). Per uno snapshot PostgreSQL c'è anche *Also load the dump into a new database*: crea un DB **nuovo** (il nome va scritto due volte) e ci fa `pg_restore`; non tocca mai un DB esistente.
6. Restore manuale di prova sulla VM (non sovrascrive mai le sorgenti):
   ```powershell
   & $exe snapshots
   & $exe restore --snapshot <id>          # finisce in C:\DupliRestore\<id>
   ```
7. **Restart agent** dalla UI: il job va a Succeeded, il servizio si riavvia da solo entro pochi secondi e l'agent torna online.
8. Test di errore (utili per vedere gli alert via email):
   - password PG sbagliata (`secret set pg-main` con un valore errato, poi Run now): job Failed e alert *BackupFailed*;
   - cartella inesistente nella policy: item Failed, gli altri item vanno avanti;
   - servizio fermo per più di 5 minuti (`sc.exe stop DupliAgent`): alert *AgentOffline* e, al riavvio, email "RESOLVED";
   - stop del servizio durante un backup: al riavvio il job viene riportato come *Interrupted*.

---

## 14. Operatività

- **Aggiornare il server:** push su `main`, poi `az containerapp update -g $RG -n $APP --image ghcr.io/azetaufficio/dupli-server:sha-<nuovo>`. Le migrazioni DB girano da sole all'avvio. Con 1 replica e revision singola c'è qualche secondo di indisponibilità: agli agent va bene, ritentano.
- **Aggiornare l'agent:** da remoto.
  1. Pubblica la release: `git tag v0.2.0 && git push origin v0.2.0`. Il workflow *Release* crea la GitHub Release con gli exe e i `.sha256`.
  2. UI → *Releases* → *Import from GitHub* (versione `0.2.0`, canale `beta` o `stable`), oppure registra a mano URL e sha256. Il server fa da mirror dell'exe.
  3. Gli agent del canale (o con la versione fissata nella tab *Updates* dell'agent) scaricano la release quando sono inattivi, verificano lo sha256 e passano la mano al Launcher. Se la nuova versione crasha o non risponde entro 5 minuti, il Launcher torna alla precedente, l'agent lo segnala e parte l'alert *AgentUpdateFailed*. Quella versione non viene più ritentata su quella VM.
  4. restic si aggiorna allo stesso modo: rendi corrente una nuova release restic; l'agent la installa e la prova sul repository prima di usarla.
  5. Il Launcher stesso non si aggiorna da solo: per aggiornarlo, lancia sulla VM `dupli-agent.exe install` (senza token) con l'exe nuovo. Ferma e riavvia il servizio da solo.
- **Rate limiting:** gli endpoint anonimi esposti su internet (`/api/agents/register`, `/api/agents/token`, `/bff/login`) accettano 30 richieste al minuto per IP client (preso da `X-Forwarded-For` dell'ingress), poi rispondono `429` con `Retry-After`. Gli agent lo trattano come errore temporaneo e ritentano. Se hai molte VM dietro lo stesso IP pubblico, alza `Dupli__RateLimiting__PermitLimit`.
- **Rinnovo segreti:** client secret Entra (scadenza impostata al punto 6), secret del mailer, password PG. Si aggiornano con `az containerapp secret set` e poi `az containerapp revision restart`.
- **Backup da fare fuori da Dupli:** DB PostgreSQL (backup automatici Azure) **e** key ring (i secret `dupli-dataprotection-*` del Key Vault: soft delete + purge protection; per una copia fuori da Azure `az keyvault secret backup`). Il DB senza key ring non basta per recuperare le password dei repository. Tieni comunque nel vault anche le password dei repository restic delle VM critiche.
- **Rimozione agent da una VM:** `& $exe uninstall` (ferma ed elimina il servizio), poi *Disable* nella UI.
