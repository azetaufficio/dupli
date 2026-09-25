# Deploy con Docker Compose

Guida per mettere in piedi il management server Dupli (API + web UI) su una singola macchina Linux con Docker, usando lo stack in `deploy/docker-compose.yml`: server, PostgreSQL e Caddy (TLS automatico via Let's Encrypt).

Adatta a un homelab, una VM singola o un piccolo deployment self-hosted. Per un deployment gestito su Azure Container Apps, vedi [`deploy-azure-container-apps.md`](deploy-azure-container-apps.md).

## Architettura

```
Browser operatori ──HTTPS──┐
                            ▼
Agent Windows/Linux ─HTTPS─► Caddy (443/80, TLS automatico) ──► server (porta 8080, rete interna)
                                                                    │
                                                                    ▼
                                                          PostgreSQL (volume dedicato)

Server/Agent ────────HTTPS──► S3 (Wasabi / Backblaze B2 / AWS / RustFS-MinIO): 1 repository restic per VM
```

Tutto lo stato persistente vive in tre volumi Docker: `pgdata` (database), `dupli-keys` (key ring di Data Protection, cifra le password dei repository e le chiavi S3 in escrow: **senza di esso i segreti nel database non sono più decifrabili**) e `dupli-tools` (cache dei binari restic).

## Prerequisiti

- Una macchina Linux con Docker Engine e il plugin `docker compose`.
- Un dominio (`DUPLI_HOST`) che punti all'IP della macchina, con le porte 80/443 raggiungibili da Internet: Caddy le usa per ottenere il certificato TLS via ACME. Per test locali puoi usare `localhost` (Caddy userà la sua CA locale, non fidata dai client fino a quando non ne importi il certificato).
- Un'app registration Microsoft Entra ID per il login degli operatori (piattaforma "Web", redirect URI `https://<DUPLI_HOST>/signin-oidc`, logout URL `https://<DUPLI_HOST>/signout-callback-oidc`, un client secret).
- Un bucket S3-compatibile (Wasabi, Backblaze B2, AWS S3, RustFS/MinIO...) dove gli agent scriveranno i repository restic — non serve configurarlo ora, si fa dalla web UI per singola VM.

## Passi

1. Clona il repository sulla macchina che ospiterà il server (o copia solo `deploy/` e il codice sorgente, necessario perché l'immagine del server viene buildata localmente da `Dockerfile`).

2. Copia e compila il file d'ambiente:

   ```bash
   cp deploy/.env.example deploy/.env
   ```

   Apri `deploy/.env` e imposta almeno:
   - `DUPLI_HOST`: il dominio pubblico (es. `backup.example.com`).
   - `POSTGRES_PASSWORD`: password del database.
   - `ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID`, `ENTRA_CLIENT_SECRET`: dall'app registration.
   - `DUPLI_BOOTSTRAP_OWNER_EMAIL`: la tua email (o UPN) — è l'unico account accettato al primo avvio, finché non esiste nessun utente operatore, e diventa il primo Owner.
   - Un canale per le e-mail di alert (`NOTIFICATIONS_CHANNEL=Smtp` con `SMTP_*`, oppure `Office365` con `O365_*`). Vedi i commenti in `deploy/.env.example` per i dettagli di ciascun canale.

3. Avvia lo stack (build incluso):

   ```bash
   docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build
   ```

   Caddy ottiene il certificato TLS al primo avvio (può richiedere qualche decina di secondi per un dominio pubblico). Segui i log con:

   ```bash
   docker compose -f deploy/docker-compose.yml logs -f
   ```

4. Apri `https://<DUPLI_HOST>` e accedi con l'email impostata in `DUPLI_BOOTSTRAP_OWNER_EMAIL`. Da **Users** puoi invitare gli altri operatori (ruoli Owner/Operator/Viewer); finché la tabella utenti è vuota può accedere solo il bootstrap owner.

5. Installa il primo agent su una VM Windows (o, per test, sul container Linux di `deploy/agent/`) seguendo il flusso di enrollment dalla pagina **Agents** della web UI (token una tantum + comando `dupli-agent install`).

## Aggiornamenti

```bash
git pull
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build
```

Il rebuild ricrea solo il container `server` (rolling restart, ~pochi secondi di downtime); `postgres` e i volumi restano intatti. Le migrazioni del database (DbUp) vengono applicate automaticamente all'avvio del server.

## Backup dello stack stesso

Lo stack non fa backup di se stesso: pianifica separatamente un backup di

- il database PostgreSQL (`pg_dump` dal container `postgres`, o uno snapshot del volume `pgdata`);
- il volume `dupli-keys` (**critico**: senza questo, le password dei repository restic e le chiavi S3 escrowate nel database sono permanentemente illeggibili anche se il database è integro);
- `deploy/.env` (contiene segreti, non è nel volume Docker).

## Ambiente di sviluppo locale

Per provare Dupli senza esporlo pubblicamente, aggiungi il profilo `dev` (avvia anche `mailpit` come catcher SMTP su `http://localhost:8025`, senza inviare mail reali):

```bash
docker compose -f deploy/docker-compose.yml --env-file deploy/.env --profile dev up -d --build
```

Con `DUPLI_HOST=localhost`, `NOTIFICATIONS_CHANNEL=Smtp`, `SMTP_HOST=mailpit`, `SMTP_PORT=1025`, `SMTP_SECURITY=None`. Per un ambiente di sviluppo end-to-end più completo (con agent Linux già enrollato, senza Docker Compose manuale), vedi la sezione "Local test stack (Aspire)" nel README principale.
