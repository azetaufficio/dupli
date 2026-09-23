# Obiettivo

Realizzare una piattaforma centralizzata per gestire backup di più VM Windows.

Ogni VM ospita:

* una o più applicazini;
* una o più cartelle dati;
* un istanza PostgreSQL con n database.

Il sistema deve permettere da una console web centrale di:

* registrare e visualizzare gli agent installati;
* configurare policy di backup;
* eseguire backup di cartelle;
* eseguire dump PostgreSQL;
* salvare i backup su storage S3;
* consultare stato, log e storico;
* avviare restore;
* aggiornare remotamente agent e componenti;
* monitorare agent offline o backup falliti.

Il motore di backup dei file deve essere **restic**.

Il progetto deve essere sviluppato principalmente in **.NET / C#**.

---

# 1. Architettura generale

La piattaforma è composta da tre elementi principali.

## Management Server

Applicazione centrale composta da:

* ASP.NET Core Web API;
* applicazione web;
* PostgreSQL come database di configurazione;
* autenticazione utenti con oidc
* API dedicate agli agent.

Il server NON deve ricevere fisicamente i backup.

Il suo ruolo è esclusivamente quello di control plane.

Responsabilità:

* configurazione agent;
* scheduling;
* policy;
* distribuzione job;
* raccolta heartbeat;
* raccolta log;
* gestione versioni;
* gestione restore;
* dashboard.

---

## Windows Agent

Applicazione .NET Worker Service installata come Windows Service.

Ogni VM effettua esclusivamente connessioni HTTPS outbound verso il Management Server.

Non devono essere aperte porte inbound sulla VM.

Responsabilità:

* registrazione al server;
* heartbeat;
* polling job;
* gestione restic;
* gestione pg_dump;
* esecuzione backup;
* esecuzione restore;
* gestione credenziali locali;
* reporting;
* aggiornamento automatico.

---

## Storage S3

I backup vengono caricati direttamente dall'agent verso S3.

Flusso:

```text
Management Server
       |
       | HTTPS
       v
 Windows Agent
       |
       +--> pg_dump
       |
       +--> restic
               |
               v
               S3
```

I dati non devono transitare attraverso il Management Server.

---

# 2. Stack tecnologico

Utilizzare:

```text
.NET 10 o ultima versione LTS disponibile

Server:
ASP.NET Core
Entity Framework Core
PostgreSQL

Agent:
.NET Worker Service
Windows Service

Web UI:
ASP.NET Core + frontend angular

Backup:
restic

PostgreSQL:
pg_dump

Object Storage:
AWS S3 / storage S3-compatible
```

Preferire una soluzione semplice, modulare e facilmente distribuibile.

---

# 3. Struttura della solution

Creare:

```text
Dupli.sln

src/

  Dupli.Server
  Dupli.Web

  Dupli.Agent
  Dupli.Updater
  Dupli.Launcher

  Dupli.Contracts
  Dupli.Domain
  Dupli.Infrastructure

tests/

  Dupli.Server.Tests
  Dupli.Agent.Tests
  Dupli.IntegrationTests
```

---

# 4. Comunicazione Agent → Server

La comunicazione deve sempre partire dall'agent.

Implementare inizialmente polling HTTPS.

API indicative:

```text
POST /api/agents/register

POST /api/agents/heartbeat

GET /api/agents/{agentId}/jobs

POST /api/jobs/{jobId}/started

POST /api/jobs/{jobId}/progress

POST /api/jobs/{jobId}/completed

POST /api/jobs/{jobId}/failed

POST /api/jobs/{jobId}/logs
```

L'agent deve effettuare polling circa ogni 30 secondi.

La frequenza deve essere configurabile.

---

# 5. Registrazione agent

Durante l'installazione deve essere fornito un enrollment token.

Esempio:

```text
BackupAgent.exe install \
  --server https://backup.example.com \
  --token ABCDEF...
```

Al primo avvio:

```text
Agent
  |
  POST /api/agents/register
  |
  v
Management Server
```

Il server genera:

```text
AgentId
AgentSecret
```

L'AgentSecret deve essere memorizzato cifrato localmente.

Su Windows utilizzare:

```text
DPAPI
```

Non salvare segreti in chiaro in JSON o registry.

---

# 6. Heartbeat

Ogni agent deve comunicare periodicamente:

```json
{
  "agentId": "...",
  "hostname": "...",
  "version": "...",
  "resticVersion": "...",
  "osVersion": "...",
  "lastBackup": "...",
  "runningJobs": [],
  "freeDiskSpace": 123456789
}
```

Il server deve considerare offline un agent che non invia heartbeat per un intervallo configurabile.

---

# 7. Modello dati principale

Creare almeno queste entità.

```text
Agent

BackupPolicy

BackupJob

BackupRun

BackupSource

PostgresDatabase

StorageTarget

AgentLog

RestoreJob

SoftwareRelease
```

Indicativamente:

## Agent

```text
Id
Name
Hostname
MachineId
Version
ResticVersion
Status
LastHeartbeat
CreatedAt
```

## BackupPolicy

```text
Id
Name
AgentId
Schedule
Enabled
RetentionPolicy
StorageTargetId
```

## BackupSource

```text
Id
BackupPolicyId
Type

Type:
Directory
PostgreSql
```

## BackupRun

```text
Id
BackupPolicyId
AgentId
StartedAt
CompletedAt
Status
BytesProcessed
SnapshotId
ErrorMessage
```

---

# 8. Backup delle cartelle

Il backup deve essere eseguito con restic.

Non implementare un motore di deduplicazione custom.

L'agent deve avere un'astrazione:

```csharp
public interface IBackupEngine
{
    Task<BackupResult> BackupAsync(
        BackupRequest request,
        CancellationToken cancellationToken);

    Task RestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken);
}
```

Prima implementazione:

```text
ResticBackupEngine
```

---

# 9. Gestione restic

Restic deve essere gestito direttamente dall'agent.

NON utilizzare:

```text
winget
chocolatey
PATH globale
```

Directory:

```text
C:\ProgramData\Dupli\tools\restic\
```

Esempio:

```text
tools\
  restic\
    0.18.1\
      restic.exe

    0.18.2\
      restic.exe
```

L'agent deve utilizzare sempre un path assoluto.

Esempio:

```text
C:\ProgramData\Dupli\tools\restic\0.18.2\restic.exe
```

---

# 10. Bootstrap restic

Alla partenza dell'agent:

```text
leggere versione restic richiesta
        |
        v
verificare presenza locale
        |
        +-- presente --> continua
        |
        +-- assente
              |
              v
          download
              |
              v
          verifica SHA256
              |
              v
          installazione directory versione
```

Il server deve pubblicare un manifest tipo:

```json
{
  "version": "0.18.2",
  "downloadUrl": "...",
  "sha256": "..."
}
```

Mai scaricare automaticamente "latest".

Le versioni devono essere sempre pinned.

---

# 11. PostgreSQL

NON effettuare backup diretto della PostgreSQL data directory.

Utilizzare:

```text
pg_dump
```

Formato consigliato:

```text
custom
```

Equivalente:

```bash
pg_dump -Fc database > database.dump
```

L'agent deve supportare configurazione di:

```text
host
port
database
username
password
pg_dump path
```

La password deve essere cifrata con DPAPI.

---

# 12. Workflow backup PostgreSQL

Esempio:

```text
Start Backup
    |
    v
Create temporary directory
    |
    v
pg_dump
    |
    +-- FAIL --> backup failed
    |
    v
restic backup
    |
    v
S3
    |
    v
delete temporary dump
    |
    v
send result to server
```

Directory temporanea:

```text
C:\ProgramData\Dupli\tmp\<job-id>\
```

Deve sempre essere ripulita anche dopo errori.

---

# 13. Repository restic

Idealmente utilizzare un repository distinto per cliente, VM o policy.

Esempio:

```text
s3:s3.amazonaws.com/dupli-backups/customer-001/server-001
```

Oppure:

```text
dupli-backups/
  customer-001/
    server-001/
```

Non hardcodare AWS.

Prevedere una futura compatibilità con storage S3-compatible.

---

# 14. Credenziali S3

I secret non devono essere restituiti continuamente dal server.

Valutare due modalità.

## MVP

Credenziali cifrate localmente tramite DPAPI.

## Evoluzione

Credenziali temporanee ottenute dal Management Server.

Preferire:

```text
temporary credentials
```

rispetto ad access key permanenti.

---

# 15. Scheduling

Lo scheduling viene configurato sul Management Server.

Esempio:

```text
giornaliero alle 02:00
```

Il server crea il job.

L'agent riceve:

```json
{
  "jobId": "...",
  "type": "backup",
  "policyId": "..."
}
```

L'agent deve garantire che lo stesso job non venga eseguito due volte.

Implementare idempotenza.

---

# 16. Concorrenza

Per MVP consentire:

```text
massimo 1 backup contemporaneo per agent
```

Evitare due processi restic sullo stesso repository se non necessario.

Prevedere una Job Queue locale.

---

# 17. Retry

Implementare retry per operazioni transienti:

```text
server non raggiungibile
S3 temporaneamente irraggiungibile
timeout HTTP
```

Utilizzare exponential backoff.

Non ritentare automaticamente errori chiaramente permanenti come:

```text
password PostgreSQL errata
directory inesistente
repository password errata
```

---

# 18. Logging

Utilizzare logging strutturato.

Ogni log deve includere quando disponibile:

```text
AgentId
JobId
BackupRunId
PolicyId
```

I log completi rimangono localmente.

Al server inviare:

```text
Info principali
Warning
Error
```

Non inviare ogni singola riga di output restic in tempo reale nella prima versione.

---

# 19. Directory locali agent

Utilizzare:

```text
C:\Program Files\Dupli\
```

solo per componenti eseguibili stabili.

Utilizzare:

```text
C:\ProgramData\Dupli\
```

per dati runtime.

Struttura:

```text
C:\Program Files\Dupli\

  Launcher\
    Dupli.Launcher.exe

  Updater\
    Dupli.Updater.exe
```

e:

```text
C:\ProgramData\Dupli\

  versions\
    1.0.0\
      Dupli.Agent.exe

    1.1.0\
      Dupli.Agent.exe

  tools\
    restic\
      0.18.2\
        restic.exe

  logs\

  tmp\

  config\

  current.json
```

---

# 20. Aggiornamento agent

Non consentire all'agent di sovrascrivere se stesso.

Utilizzare:

```text
Launcher
Agent
Updater
```

Il Launcher è stabile e molto piccolo.

Il file:

```text
current.json
```

contiene:

```json
{
  "agentVersion": "1.1.0"
}
```

Il launcher avvia:

```text
versions\1.1.0\Dupli.Agent.exe
```

---

# 21. Processo update

Workflow:

```text
Agent rileva nuova versione
        |
        v
download package
        |
        v
verify SHA256 / firma
        |
        v
extract versions\1.2.0\
        |
        v
Updater
        |
        v
stop Windows Service
        |
        v
modify current.json
        |
        v
start Windows Service
        |
        v
health check
```

---

# 22. Rollback

Conservare almeno la versione precedente.

Se l'agent aggiornato:

```text
non parte
crasha
non invia heartbeat
```

l'updater deve ripristinare:

```text
current.json
```

alla versione precedente.

Esempio:

```text
1.2.0 -> fallisce
1.1.0 -> rollback
```

---

# 23. Release channels

Prevedere:

```text
dev
beta
stable
```

Ogni agent appartiene a un channel.

Esempio:

```text
server-test     beta
cliente-001     stable
cliente-002     stable
```

Il server decide la versione target.

---

# 24. Rollout

Prevedere in futuro rollout progressivo.

Esempio:

```text
5%
20%
50%
100%
```

Per MVP è sufficiente poter assegnare manualmente una versione o release channel.

---

# 25. Restore

Il server deve mostrare gli snapshot disponibili.

Workflow:

```text
Web UI
   |
   v
select snapshot
   |
   v
select files / complete restore
   |
   v
RestoreJob
   |
   v
Agent
   |
   v
restic restore
```

Per sicurezza, la prima versione deve effettuare restore su directory alternativa.

Esempio:

```text
C:\DupliRestore\<job-id>\
```

NON sovrascrivere automaticamente dati di produzione.

---

# 26. Restore PostgreSQL

Per PostgreSQL il restore iniziale deve:

```text
ripristinare database.dump su filesystem
```

Non eseguire automaticamente:

```text
drop database
pg_restore
```

sul database di produzione.

Il restore automatico del DB può essere implementato successivamente con conferma esplicita.

---

# 27. Dashboard Web

Pagina principale:

```text
Agents

Online
Offline
Backup failed
Backup running
Update required
```

Tabella:

```text
Nome
Hostname
Versione agent
Versione restic
Ultimo heartbeat
Ultimo backup
Stato
```

---

# 28. Pagina Agent

Mostrare:

```text
hostname
OS
agent version
restic version
ultimo heartbeat
spazio disco
backup policies
backup history
logs
```

Azioni:

```text
Run backup now
Update agent
Restart agent
Restore
```

Il comando "Restart agent" deve essere implementato tramite job ricevuto dall'agent, non tramite connessione inbound.

---

# 29. Pagina Backup Policy

Configurazione:

```text
Nome

Cartelle:
D:\Dati
D:\Uploads

PostgreSQL:
host
port
database
username

Schedule:
02:00

Retention:
daily
weekly
monthly

Storage Target
```

---

# 30. Retention

Utilizzare restic.

Configurazione esempio:

```text
keep daily: 7
keep weekly: 4
keep monthly: 12
```

Comando equivalente:

```text
restic forget
--keep-daily 7
--keep-weekly 4
--keep-monthly 12
--prune
```

Non eseguire prune dopo ogni singolo backup.

Renderlo un job separato schedulabile.

---

# 31. Check repository

Implementare un job periodico:

```text
restic check
```

Esempio:

```text
settimanale
```

Il risultato deve essere visibile nel Management Server.

---

# 32. VSS

Non implementare VSS nella prima milestone.

Preparare però l'architettura per poterlo aggiungere.

Creare un'interfaccia:

```csharp
public interface IFileSnapshotProvider
{
    Task<FileSnapshot> CreateAsync(...);
}
```

Prima implementazione:

```text
DirectFileSnapshotProvider
```

Successivamente:

```text
VssFileSnapshotProvider
```

---

# 33. Sicurezza

Obbligatorio:

```text
HTTPS
TLS moderno
password cifrate
token agent separati
secret rotation possibile
checksum download
signed agent binaries
```

L'agent non deve mai eseguire comandi shell arbitrari ricevuti dal server.

I job ammessi devono essere tipizzati:

```text
Backup
Restore
RepositoryCheck
Retention
AgentUpdate
ResticUpdate
```

Mai implementare:

```json
{
  "command": "powershell ..."
}
```

---

# 34. Firma dei binari

Prevedere firma Authenticode di:

```text
Launcher
Updater
Agent
```

L'updater deve verificare che il pacchetto ricevuto sia valido prima di installarlo.

---

# 35. API contracts

Definire tutti i DTO in:

```text
Dupli.Contracts
```

Esempi:

```text
RegisterAgentRequest
RegisterAgentResponse

HeartbeatRequest

AgentJobDto

BackupJobDto

RestoreJobDto

BackupResultDto

UpdateManifestDto
```

Non condividere direttamente le Entity EF tra server e agent.

---

# 36. Stato dei job

Usare una state machine semplice:

```text
Pending

Assigned

Running

Succeeded

Failed

Cancelled
```

Prevedere anche:

```text
TimedOut
```

---

# 37. Cancellazione job

L'utente deve poter richiedere:

```text
Cancel backup
```

Il server imposta lo stato di cancellazione.

L'agent lo rileva e cancella il processo tramite:

```text
CancellationToken
Process.Kill(...)
```

quando necessario.

---

# 38. Monitoring

Il server deve evidenziare:

```text
agent offline

backup non eseguito

backup fallito

backup troppo vecchio

repository check fallito

agent outdated
```

Preparare il dominio per future notifiche:

```text
email
Teams
Telegram
webhook
```

Non necessario implementarle tutte nell'MVP.

---

# 39. Milestone 1 — Agent locale

Realizzare per prima cosa senza Management Server.

Obiettivo:

```text
Worker Service
       |
       + pg_dump
       |
       + restic
       |
       + S3
```

Configurazione JSON locale temporanea.

Funzioni:

```text
backup directory
backup PostgreSQL
logging
restore
retention
```

Questa milestone deve dimostrare l'intero backup end-to-end.

---

# 40. Milestone 2 — Management API

Implementare:

```text
registrazione agent
heartbeat
agent list
job queue
backup results
```

Collegare l'agent al server.

---

# 41. Milestone 3 — Web UI

Realizzare:

```text
dashboard
agents
backup policies
backup history
logs
run now
```

---

# 42. Milestone 4 — Update infrastructure

Implementare:

```text
Launcher
Updater
version directories
restic manager
agent update
restic update
rollback
```

---

# 43. Milestone 5 — Restore

Implementare:

```text
snapshot list
browse snapshot
restore directory
restore PostgreSQL dump
```

---

# 44. Milestone 6 — Hardening

Implementare e testare:

```text
network disconnect

server unavailable

S3 unavailable

PostgreSQL unavailable

disk full

agent restart during backup

Windows reboot

failed update

rollback

corrupt download

duplicate job

expired credentials
```

---

# 45. Vincoli importanti

Non reinventare funzionalità già fornite da restic.

Non implementare:

```text
deduplicazione custom
compressione custom
repository format custom
chunking custom
encryption custom
```

La piattaforma deve essere principalmente:

```text
CONTROL PLANE
+
ORCHESTRATOR
```

di strumenti affidabili.

---

# 46. Priorità progettuali

Ordine di priorità:

```text
1. Restore affidabile
2. Backup affidabile
3. Sicurezza
4. Osservabilità
5. Aggiornamenti affidabili
6. UX
```

Considerare un backup valido solo se è possibile dimostrarne il restore.

---

# 47. Definition of Done MVP

La prima versione può essere considerata utilizzabile quando è possibile:

1. installare l'agent su una nuova VM Windows;
2. registrarlo tramite enrollment token;
3. vederlo online nella console;
4. configurare una cartella;
5. configurare PostgreSQL;
6. configurare S3;
7. schedulare un backup;
8. effettuare pg_dump;
9. effettuare backup restic;
10. vedere successo/fallimento sul server;
11. consultare lo storico;
12. effettuare un restore su directory alternativa;
13. aggiornare remotamente l'agent;
14. aggiornare remotamente restic;
15. fare rollback automatico dell'agent dopo update fallito.

---

# 48. Prima attività da eseguire

Iniziare creando la solution e implementando esclusivamente questo vertical slice:

```text
Windows Worker Service
        |
        v
BackupJob locale
        |
        +--> pg_dump
        |
        +--> restic backup
        |
        v
S3
        |
        v
BackupResult
```

Non iniziare dalla UI.

Prima dimostrare che:

```text
backup
restore
error handling
```

funzionano correttamente da una VM Windows reale.

Solo dopo aggiungere API centrale e Web UI.

---

# Principio guida

Ogni componente deve essere sostituibile.

L'Agent non deve conoscere dettagli della UI.

Il Management Server non deve conoscere dettagli del processo restic.

Usare interfacce per:

```text
IBackupEngine

IDatabaseBackupProvider

IStorageCredentialProvider

IFileSnapshotProvider

IToolManager

IAgentUpdater
```

La prima implementazione sarà:

```text
ResticBackupEngine
PostgresDumpProvider
S3CredentialProvider
DirectFileSnapshotProvider
ResticToolManager
VersionedAgentUpdater
```

L'obiettivo è ottenere una piattaforma semplice da mantenere, sicura, facilmente distribuibile su decine o centinaia di VM Windows e che possa evolvere senza dover riscrivere il motore di backup.
