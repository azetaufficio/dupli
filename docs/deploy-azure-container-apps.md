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
                           │   ├─ /var/lib/dupli/keys  ← Azure Files (key ring Data Protection)
                           │   └─ /var/lib/dupli/tools ← effimero (cache mirror restic)
                           ▼
              Azure Database for PostgreSQL – Flexible Server (DB "dupli")

Agent Windows ──HTTPS──► S3 (Wasabi / B2): 1 repository restic per VM
```

Vincoli da rispettare (dipendono da com'è fatto il server oggi):

| Vincolo | Motivo |
|---|---|
| **Esattamente 1 replica** (`minReplicas = maxReplicas = 1`) | Scheduler, sweeper e alert girano dentro il processo: con 2 repliche si avrebbero notifiche doppie e, senza `SigningKey` configurata, token agent non validi tra repliche. Con 0 repliche lo scheduler non gira. |
| **Key ring Data Protection su volume persistente, separato dal DB** | Cifra password dei repository e chiavi S3 in escrow. Se si perde il key ring, i segreti in DB non sono più decifrabili: va salvato a parte. |
| **Scegliere l'URL definitivo prima di installare agent** | L'URL del server viene salvato in `agent.json` all'enrollment. Se il dominio cambia dopo, gli agent vanno ri-enrollati (o `agent.json` modificato a mano). |
| **HTTPS obbligatorio** | Il login OIDC con Entra ID richiede redirect URI `https://`. ACA termina il TLS e passa `X-Forwarded-Proto`, che il server usa. |

Costo indicativo (verificare con il calcolatore Azure): ACA consumption con 1 replica sempre attiva da 0,5 vCPU / 1 GiB, più PostgreSQL Flexible Burstable B1ms, più pochi centesimi di Azure Files e Log Analytics.

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
  az provider register --namespace Microsoft.Storage
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
STG=duplikeys$RANDOM           # 3-24 caratteri minuscoli/numeri, univoco
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

## 4. Storage per il key ring (Azure Files)

```bash
az storage account create -g $RG -n $STG -l $LOC --sku Standard_LRS --kind StorageV2 \
  --min-tls-version TLS1_2 --allow-blob-public-access false

az storage share-rm create -g $RG --storage-account $STG -n dupli-keys --quota 1

STG_KEY=$(az storage account keys list -g $RG -n $STG --query "[0].value" -o tsv)
```

Proteggi la share dalla cancellazione accidentale: soft delete delle file share (attivo di default sugli account nuovi, controlla) e un **resource lock** sul resource group o sull'account. Dopo il primo avvio scarica una copia dei file `key-*.xml` e mettila nel password vault: è il backup del key ring.

---

## 5. Ambiente Container Apps

```bash
az containerapp env create -g $RG -n $ENV_NAME -l $LOC

az containerapp env storage set -g $RG -n $ENV_NAME \
  --storage-name dupli-keys \
  --azure-file-account-name $STG --azure-file-account-key "$STG_KEY" \
  --azure-file-share-name dupli-keys --access-mode ReadWrite

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
5. Limita chi può entrare. Scegli **una** delle due strade:
   - **Semplice:** Enterprise applications → Dupli → Properties → *Assignment required* = **Yes**, poi in *Users and groups* aggiungi gli operatori (o un gruppo).
   - **Con ruolo:** in App registration → App roles, crea il ruolo `Dupli.Operator` (Allowed member types: Users/Groups; Value: `Dupli.Operator`). Assegnalo in Enterprise applications → Users and groups, e imposta `ENTRA_REQUIRED_ROLE=Dupli.Operator`. Conviene tenere anche *Assignment required* = Yes.

### Da CLI (alternativa)
```bash
ENTRA_TENANT_ID=$(az account show --query tenantId -o tsv)
ENTRA_CLIENT_ID=$(az ad app create --display-name Dupli --sign-in-audience AzureADMyOrg \
  --web-redirect-uris "https://$FQDN/signin-oidc" "https://$FQDN/signout-callback-oidc" \
  --query appId -o tsv)
az ad sp create --id $ENTRA_CLIENT_ID
ENTRA_CLIENT_SECRET=$(az ad app credential reset --id $ENTRA_CLIENT_ID --display-name dupli-server --years 1 --query password -o tsv)
```
Poi fai il passo 5 (assignment/ruolo) dal portale.

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
export LOC ENV_ID IMAGE FQDN DB_CONNECTION SIGNING_KEY ENTRA_TENANT_ID ENTRA_CLIENT_ID ENTRA_CLIENT_SECRET
export ENTRA_REQUIRED_ROLE=""            # oppure Dupli.Operator
export O365_TENANT_ID=... O365_CLIENT_ID=... O365_CLIENT_SECRET=... O365_FROM=dupli@tuodominio.it O365_TO=ops@tuodominio.it
```

Il template è nel repo: `deploy/azure/containerapp.template.yaml`, riportato qui sotto. Non contiene segreti: i valori arrivano da `envsubst`.

```yaml
location: ${LOC}
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
          - name: Dupli__Auth__Mode
            value: EntraId
          - name: Dupli__Auth__EntraId__TenantId
            value: ${ENTRA_TENANT_ID}
          - name: Dupli__Auth__EntraId__ClientId
            value: ${ENTRA_CLIENT_ID}
          - name: Dupli__Auth__EntraId__ClientSecret
            secretRef: entra-client-secret
          - name: Dupli__Auth__EntraId__RequiredRole
            value: "${ENTRA_REQUIRED_ROLE}"
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
        volumeMounts:
          - volumeName: keys
            mountPath: /var/lib/dupli/keys
    scale:
      minReplicas: 1
      maxReplicas: 1
    volumes:
      - name: keys
        storageType: AzureFile
        storageName: dupli-keys
        # Il container gira come utente "app" (uid 1654): la share deve essere scrivibile da lui.
        mountOptions: "uid=1654,gid=1654,dir_mode=0700,file_mode=0600"
```

Il template non imposta `Dupli__Admin__ApiKey`, quindi la chiave admin resta disattivata: in produzione si usa solo il login Entra. Se ti serve per degli script, aggiungila come secret.

Crea l'app senza lasciare file con segreti su disco:
```bash
envsubst < deploy/azure/containerapp.template.yaml > /tmp/dupli-app.yaml
az containerapp create -g $RG -n $APP --yaml /tmp/dupli-app.yaml
rm /tmp/dupli-app.yaml
```

Se la CLI rifiuta `mountOptions` (versioni vecchie), togli la riga, crea l'app e controlla i log. Se compare un errore di permessi su `/var/lib/dupli/keys`, aggiorna la CLI e riapplica.

### Verifica del deploy
```bash
curl -s https://$FQDN/health                    # {"status":"ok"}
az containerapp logs show -g $RG -n $APP --follow --tail 100
```
Nei log del primo avvio devi vedere:
- `Applied database script ...0001_initial.sql` e `...0002_m3.sql` (le migrazioni DbUp sul DB Azure);
- **nessun** errore Data Protection sulla directory keys;
- nessun `Fatal:`. In quel caso il container esce e ACA lo riavvia: leggi il messaggio, di solito è configurazione mancante.

Poi dal browser:
1. `https://<FQDN>` ti porta al login Microsoft e poi alla dashboard vuota.
2. In alto a destra compare il tuo nome; *Sign out* ti riporta al login.
3. Un utente non assegnato all'app viene bloccato da Entra (o vede "not authorized" se usi il ruolo).

Dopo il primo avvio controlla che la share `dupli-keys` contenga un file `key-<guid>.xml`: portale → Storage account → File shares. Salvane una copia nel vault.

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
1. Scarica l'exe: GitHub → Actions → ultima run verde su `main` → artifact **dupli-agent-win-x64**, oppure `gh run download -n dupli-agent-win-x64`. Copia `dupli-agent.exe` sulla VM, per esempio in `C:\Temp`.
2. Apri **PowerShell come Amministratore**:
   ```powershell
   cd C:\Temp
   Unblock-File .\dupli-agent.exe          # exe non ancora firmato: evita il blocco "scaricato da internet"
   .\dupli-agent.exe install --server https://<FQDN> --token <TOKEN>
   sc.exe start DupliAgent
   ```
   L'install fa l'enrollment e salva segreti, password del repository e chiave S3 con DPAPI. Poi copia l'exe in `C:\ProgramData\Dupli\versions\0.1.0.0\` e registra il servizio `DupliAgent` (LocalSystem, avvio automatico, riavvio automatico in caso di crash). **Non avvia il servizio**: per quello serve `sc.exe start`.
   Se Defender o SmartScreen bloccano l'exe (non firmato), aggiungi un'eccezione per quel file. La firma arriva con la M4.
3. Se farai backup PostgreSQL, salva la password del DB con lo stesso nome che userai nella policy (campo *Password secret*, es. `pg-main`):
   ```powershell
   $exe = "C:\ProgramData\Dupli\versions\0.1.0.0\dupli-agent.exe"
   $p = Read-Host "Password PostgreSQL" -AsSecureString
   [Net.NetworkCredential]::new('', $p).Password | & $exe secret set pg-main
   ```
4. Entro circa 30 secondi, nella UI l'agent passa a **Active / online** con hostname, OS, versione e spazio disco. Nella tab *Logs* compaiono i log caricati dall'agent.
   Log locali: `C:\ProgramData\Dupli\logs\`. Stato del servizio: `sc.exe query DupliAgent`.

---

## 13. Prima policy e verifiche end-to-end

1. Agent → **New policy**:
   - cartelle, per esempio `D:\Dati` (una per riga), con eventuali esclusioni;
   - PostgreSQL: host `localhost`, porta, utente, *Password secret* `pg-main`, database esclusi;
   - cron, per esempio `0 2 * * *` con fuso `Europe/Rome`: l'anteprima mostra le prossime esecuzioni;
   - retention daily/weekly/monthly.
2. **Run now** sulla policy. Nella tab *Jobs* lo stato passa Pending → Running → Succeeded; nella tab *Backup history* compaiono snapshot e byte.
3. **Run restore test**: nella tab *Restore tests* ogni file del campione e ogni DB devono risultare Succeeded.
4. **Check repository**: Succeeded.
5. Restore manuale di prova sulla VM (non sovrascrive mai le sorgenti):
   ```powershell
   & $exe snapshots
   & $exe restore --snapshot <id>          # finisce in C:\DupliRestore\<id>
   ```
6. **Restart agent** dalla UI: il job va a Succeeded, il servizio si riavvia da solo entro pochi secondi e l'agent torna online.
7. Test di errore (utili per vedere gli alert via email):
   - password PG sbagliata (`secret set pg-main` con un valore errato, poi Run now): job Failed e alert *BackupFailed*;
   - cartella inesistente nella policy: item Failed, gli altri item vanno avanti;
   - servizio fermo per più di 5 minuti (`sc.exe stop DupliAgent`): alert *AgentOffline* e, al riavvio, email "RESOLVED";
   - stop del servizio durante un backup: al riavvio il job viene riportato come *Interrupted*.

---

## 14. Operatività

- **Aggiornare il server:** push su `main`, poi `az containerapp update -g $RG -n $APP --image ghcr.io/azetaufficio/dupli-server:sha-<nuovo>`. Le migrazioni DB girano da sole all'avvio. Con 1 replica e revision singola c'è qualche secondo di indisponibilità: agli agent va bene, ritentano.
- **Aggiornare l'agent:** per ora a mano, finché la M4 non porta l'updater:
  1. `sc.exe stop DupliAgent`;
  2. `.\dupli-agent.exe install --server ... --token <nuovo token>` con il nuovo exe (serve un nuovo token);
  3. oppure sostituisci l'exe nel percorso registrato dal servizio.
- **Rinnovo segreti:** client secret Entra (scadenza impostata al punto 6), secret del mailer, password PG. Si aggiornano con `az containerapp secret set` e poi `az containerapp revision restart`.
- **Backup da fare fuori da Dupli:** DB PostgreSQL (backup automatici Azure) **e** key ring (copia dei `key-*.xml`). Il DB senza key ring non basta per recuperare le password dei repository. Tieni comunque nel vault anche le password dei repository restic delle VM critiche.
- **Rimozione agent da una VM:** `& $exe uninstall` (ferma ed elimina il servizio), poi *Disable* nella UI.
