# Local test copy of live ERPNext

This builds an **exact copy** of the live ERPNext (erpnext 15.114.0, frappe 15.113.0, posawesome 15.35.2) on a Windows laptop, with the same data and settings. TillPOS is developed and tested against this copy — **never against live**.

**Why copy the live Docker image instead of downloading the versions from git:** the image already contains the exact code of all three apps (including the exact POS Awesome commit and any custom app you may have), so the copy cannot drift from live. Building from git is described at the end as a fallback.

**What carries the "settings":** everything you configure in ERPNext — System/Stock/Accounts Settings, POS Profiles, Pricing Rules, tax templates, users and roles, custom fields, print formats, POS Awesome settings — lives **in the database**. The database backup brings all of it. Only three things live outside the database: uploaded files (backed up with `--with-files`), the site's `encryption_key` (in `site_config.json`), and the app code (in the Docker image).

> ⚠️ The copy contains real customer data and password hashes. Keep the laptop encrypted (BitLocker), don't share the backup files, and delete them when no longer needed.

---

## Part A — On the live server (SSH)

Run these on the server. Replace `<backend>` and `<db>` with your container names and `<site>` with your site name.

### A1. Find containers, site and versions

```bash
docker ps --format 'table {{.Names}}\t{{.Image}}'
# note the backend container (e.g. erpnext-backend-1) and the db container (e.g. erpnext-db-1)

docker exec <backend> ls sites                      # your site is the folder that is not apps.txt/assets/common_site_config.json
docker exec <backend> bench version                 # confirm 15.113.0 / 15.114.0 / 15.35.2
docker exec <backend> ls apps                       # any app besides frappe, erpnext, posawesome? note it
docker inspect --format '{{.Config.Image}}' <backend>   # → ERP_IMAGE for .env
docker exec <db> mariadb --version || docker exec <db> mysql --version   # → MARIADB_IMAGE, e.g. 10.6.x → mariadb:10.6
```

### A2. Take a full backup (database + files + site config)

```bash
docker exec <backend> bench --site <site> backup --with-files --compress
mkdir -p ~/erp-copy
docker cp <backend>:/home/frappe/frappe-bench/sites/<site>/private/backups/. ~/erp-copy/
ls -lh ~/erp-copy
```

Keep only the **latest set** of four files (same timestamp prefix):
`*-database.sql.gz`, `*-files.tar`, `*-private-files.tar`, `*-site_config_backup.json`.

### A3. Save the Docker image

```bash
docker save <ERP_IMAGE from A1> | gzip > ~/erp-copy/erp-image.tar.gz     # roughly 1–3 GB
```

### A4. Download everything to the laptop

On the laptop (PowerShell):

```powershell
mkdir D:\erp-test\backup
scp user@SERVER_IP:~/erp-copy/* D:\erp-test\backup\
```

(Or use WinSCP.) Afterwards, delete `~/erp-copy` on the server.

---

## Part B — Prepare the laptop (one time)

### B1. Turn on CPU virtualization

Task Manager → Performance → CPU → **Virtualization** must say *Enabled*. If it says *Disabled*: reboot into BIOS/UEFI and enable **Intel Virtualization Technology (VT-x)**.

### B2. Install WSL 2 and Docker Desktop

PowerShell **as Administrator**:

```powershell
wsl --install
# reboot when asked
winget install --id Docker.DockerDesktop -e
```

Start Docker Desktop and wait until it says *Engine running*.

### B3. Keep Docker's data on D: (C: is almost full)

Docker Desktop → Settings → Resources → Advanced → **Disk image location** → `D:\DockerData` → Apply & restart.
Then Settings → Resources → set Memory to **8 GB** (enough for ERPNext; leaves the rest for Windows).

---

## Part C — Start the copy

### C1. Load the image and configure

```powershell
docker load -i D:\erp-test\backup\erp-image.tar.gz
cd D:\XAMPP\htdocs\ERP-NEXT\deploy\erp-test
copy .env.example .env
notepad .env      # set ERP_IMAGE, MARIADB_IMAGE, DB_ROOT_PASSWORD (local only)
```

### C2. Start the containers

```powershell
docker compose -p erptest --env-file .env up -d
docker compose -p erptest ps          # configurator: Exited (0); all others: running
```

### C3. Create the site and restore the live backup

Copy the backup into the container:

```powershell
docker cp D:\erp-test\backup\. erptest-backend-1:/tmp/backup/
docker exec -u root erptest-backend-1 chown -R frappe:frappe /tmp/backup
docker exec -it erptest-backend-1 bash
```

Inside the container (replace `<ts>` with the backup's timestamp prefix and `<pw>` with DB_ROOT_PASSWORD):

```bash
cd /home/frappe/frappe-bench
bench new-site erp-test.local --mariadb-root-password <pw> --admin-password admin --mariadb-user-host-login-scope='%'
bench --site erp-test.local set-config mute_emails 1        # BEFORE restore: never email real customers
bench --site erp-test.local restore /tmp/backup/<ts>-database.sql.gz \
    --with-public-files /tmp/backup/<ts>-files.tar \
    --with-private-files /tmp/backup/<ts>-private-files.tar \
    --mariadb-root-password <pw>
cat /tmp/backup/<ts>-site_config_backup.json | grep encryption_key   # copy the value
bench --site erp-test.local set-config encryption_key '<value from live>'
bench --site erp-test.local set-config mute_emails 1        # again: restore can overwrite config
bench --site erp-test.local set-config host_name http://localhost:8080
bench --site erp-test.local disable-scheduler
bench --site erp-test.local migrate                          # same versions → should finish quickly
bench --site erp-test.local set-maintenance-mode off
bench use erp-test.local
exit
```

(If a flag is rejected by your bench version, use `--db-root-password` instead of `--mariadb-root-password`.)

### C4. Cut the copy off from the outside world

Open http://localhost:8080 and log in with a live user (passwords are the same as live; Administrator's too). Then, **before anything else**:

1. **Webhook** list → disable every webhook (they fire on save and would call real services).
2. **Email Account** list → untick *Enable Incoming* / *Enable Outgoing* on every account.
3. Any payment, SMS or WhatsApp integration settings → disable.
4. Optional: `docker exec erptest-backend-1 bench --site erp-test.local set-admin-password <new>` to use a different Administrator password on the copy.

Check: you see the live items, prices, POS Profiles and Pricing Rules, and POS Awesome opens.

---

## Part D — What to do next (TillPOS plan, Task 1 steps 4–7)

Follow **Task 1, steps 4–7** in `docs/superpowers/plans/2026-10-05-tillpos-plan1-engine-and-catalog-sync.md` on this copy:

- Step 4: Allow Negative Stock; role **TillPOS Device**; user `till1@shop.local` with API key/secret; a POS Profile for Till 1 with write-off limit 0.05.
- Steps 5–6: run the API checks and fill in `docs/erp-api-notes.md`, **plus** the extra checks listed in `docs/superpowers/plans/2026-10-05-tillpos-plan1-followups.md` §1 (exact rounding-method text, "Round Tax Amount Row-wise", Deleted Document permission, net-rate tax bands, variants, UOM-specific offers).

Then send the notes back so the TillPOS `pull` / `scan` / `parity` runs (plan Task 12 steps 4–9) can be done.

---

## Daily use

```powershell
cd D:\XAMPP\htdocs\ERP-NEXT\deploy\erp-test
docker compose -p erptest stop        # free RAM when not testing
docker compose -p erptest start
```

**Refresh with newer live data:** repeat A2/A4, then inside the container `bench --site erp-test.local restore ...` again (C3 from `set-config mute_emails`), then C4 again.

**Throw the copy away completely:** `docker compose -p erptest down -v` (deletes the local database and files).

---

## Fallback — build the same versions from git (only if copying the image is impossible)

Use frappe_docker's custom-image build with these pins (`apps.json`):

```json
[
  { "url": "https://github.com/frappe/erpnext", "branch": "v15.114.0" },
  { "url": "<POS Awesome repo URL used on live>", "branch": "<tag or commit for 15.35.2>" }
]
```

Frappe itself is pinned with `--build-arg=FRAPPE_BRANCH=v15.113.0`. Get the exact POS Awesome repository and commit from the live server first — several forks use the "posawesome" name:

```bash
docker exec <backend> bash -c 'cd apps/posawesome && git remote -v; git log -1 --format=%H' 
docker exec <backend> cat apps/posawesome/posawesome/__init__.py
```

If the image has no `.git` folders, ask whoever built the live image for its `apps.json`. Any other custom app on live must be added too, or the restore will fail.
