# Local test copy of live ERPNext (installed from git, no Docker)

Goal: the same versions as live — **frappe 15.113.0, erpnext 15.114.0, posawesome 15.35.2** — installed from the official git repositories with `bench`, then the **live database restored into it**. TillPOS is developed and tested against this copy, never against live.

**Why WSL:** Frappe/ERPNext does not run natively on Windows (it needs Linux: Redis, MariaDB, process forking). WSL 2 runs a real Ubuntu inside Windows; it is not Docker. `bench` inside Ubuntu is the official way to install ERPNext.

**What the restore brings:** everything configured in ERPNext — System/Stock/Accounts Settings, POS Profiles, Pricing Rules, tax templates, users and roles, custom fields, print formats, POS Awesome settings — is stored in the database, so the live backup brings all of it. Outside the database there are only uploaded files (backed up with `--with-files`), the site's `encryption_key` (in `site_config.json`), and the app code (installed from git below).

> ⚠️ The copy contains real customer data and live password hashes. Keep the laptop encrypted (BitLocker), don't share the backup files, delete them when done.

---

## Part A — On the live server: export the database and settings

Live runs in Docker, so the commands go through the backend container. Replace `<backend>` and `<site>`.

### A1. Find the container, site, versions and the exact POS Awesome source

```bash
docker ps --format 'table {{.Names}}\t{{.Image}}'       # note the backend container, e.g. erpnext-backend-1
docker exec <backend> ls sites                           # your site = the folder that is not apps.txt / assets / common_site_config.json
docker exec <backend> bench version                      # expect frappe 15.113.0, erpnext 15.114.0, posawesome 15.35.2
docker exec <backend> ls apps                            # any app other than frappe, erpnext, posawesome must also be installed locally
docker exec <backend> bash -c 'cd apps/posawesome && git remote -v; git log -1 --format="%H %d"' 
docker exec <db-container> mariadb --version             # local MariaDB should be the same major.minor (10.6 recommended)
```

**Important:** several forks are called "posawesome". Use the repository URL and commit printed above. If the container has no `.git` folder, ask whoever built the live image for its `apps.json` (it lists the exact repo and branch/tag of every app).

### A2. Take a full backup

```bash
docker exec <backend> bench --site <site> backup --with-files --compress
mkdir -p ~/erp-copy
docker cp <backend>:/home/frappe/frappe-bench/sites/<site>/private/backups/. ~/erp-copy/
ls -lh ~/erp-copy
```

Keep the **latest** set of four files (same timestamp prefix): `*-database.sql.gz`, `*-files.tar`, `*-private-files.tar`, `*-site_config_backup.json`.

### A3. Download to the laptop

Laptop, PowerShell:

```powershell
mkdir D:\erp-test\backup
scp user@SERVER_IP:~/erp-copy/* D:\erp-test\backup\
```

Then delete `~/erp-copy` on the server.

---

## Part B — Install Ubuntu (WSL 2) on the laptop

1. **Virtualization on:** Task Manager → Performance → CPU → *Virtualization: Enabled*. If disabled, enable **Intel VT-x** in BIOS/UEFI.
2. PowerShell **as Administrator**:
   ```powershell
   wsl --install -d Ubuntu-22.04
   ```
   Reboot when asked; Ubuntu opens and asks for a Linux username and password.
3. **Keep it on D:** (C: has only ~19 GB free). PowerShell:
   ```powershell
   wsl --shutdown
   wsl --manage Ubuntu-22.04 --move D:\WSL\Ubuntu-22.04
   ```
   (If `--manage` is not recognised, run `wsl --update` first.)

Ubuntu 22.04 is used because its default MariaDB (10.6) and Python (3.10) are both supported by Frappe v15.

---

## Part C — Install the prerequisites (inside Ubuntu)

Open "Ubuntu-22.04" from the Start menu; all commands below run there.

```bash
sudo apt update && sudo apt -y upgrade
sudo apt -y install git curl build-essential pkg-config \
  python3-dev python3-venv python3-pip pipx \
  mariadb-server mariadb-client libmysqlclient-dev \
  redis-server xvfb libfontconfig1 wkhtmltopdf cron
```

### MariaDB settings required by Frappe

```bash
sudo tee /etc/mysql/mariadb.conf.d/99-frappe.cnf > /dev/null <<'EOF'
[mysqld]
character-set-client-handshake = FALSE
character-set-server = utf8mb4
collation-server = utf8mb4_unicode_ci

[mysql]
default-character-set = utf8mb4
EOF
sudo systemctl restart mariadb          # if systemctl is unavailable: sudo service mariadb restart
sudo mariadb -e "ALTER USER 'root'@'localhost' IDENTIFIED BY 'LOCAL_DB_PASSWORD'; FLUSH PRIVILEGES;"
sudo systemctl enable --now redis-server
```

Use your own local password instead of `LOCAL_DB_PASSWORD` (local copy only — not the live one).

### Node 18 + Yarn, and bench

```bash
curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/v0.39.7/install.sh | bash
source ~/.bashrc
nvm install 18 && nvm alias default 18
npm install -g yarn
pipx ensurepath && source ~/.bashrc
pipx install frappe-bench
bench --version
```

---

## Part D — Install the exact versions from the official git repositories

```bash
cd ~
bench init --frappe-branch v15.113.0 frappe-bench
cd frappe-bench
bench get-app --branch v15.114.0 erpnext https://github.com/frappe/erpnext
bench get-app --branch <tag-or-branch> posawesome <POS Awesome repo URL from A1>
# if A1 printed a commit hash instead of a tag:
#   cd apps/posawesome && git fetch --unshallow 2>/dev/null; git checkout <commit> && cd ../.. && bench build --app posawesome
bench version          # must show the same three versions as live
```

Install any other app listed in A1 the same way, or the restore will fail.

---

## Part E — Create the site and restore the live backup

Copy the backup into Ubuntu (Windows drives are under `/mnt/`):

```bash
mkdir -p ~/backup && cp /mnt/d/erp-test/backup/* ~/backup/ && ls ~/backup
cd ~/frappe-bench
bench new-site erp-local.test --mariadb-root-password LOCAL_DB_PASSWORD --admin-password admin
bench --site erp-local.test set-config mute_emails 1            # before restore: never email real customers
bench --site erp-local.test restore ~/backup/<ts>-database.sql.gz \
    --with-public-files ~/backup/<ts>-files.tar \
    --with-private-files ~/backup/<ts>-private-files.tar \
    --mariadb-root-password LOCAL_DB_PASSWORD
grep encryption_key ~/backup/<ts>-site_config_backup.json        # copy the value
bench --site erp-local.test set-config encryption_key '<value from live>'
bench --site erp-local.test set-config mute_emails 1             # again, restore may overwrite config
bench --site erp-local.test disable-scheduler                    # no background jobs/emails/integrations
bench --site erp-local.test migrate                              # same versions → quick
bench --site erp-local.test set-maintenance-mode off
bench use erp-local.test
bench start
```

(`<ts>` = the timestamp prefix of the backup files. If your bench rejects `--mariadb-root-password`, use `--db-root-password`.)

Open **http://localhost:8000** in Windows and log in with a live user (same passwords as live, Administrator included).

### Cut the copy off from the outside world — do this first

1. **Webhook** list → disable all webhooks (they fire when documents are saved).
2. **Email Account** list → untick *Enable Incoming* and *Enable Outgoing* on every account.
3. Payment / SMS / WhatsApp or other integration settings → disable.
4. Optional: `bench --site erp-local.test set-admin-password <new>` to use a different Administrator password on the copy.

Check that items, prices, POS Profiles and Pricing Rules match live and that POS Awesome opens.

---

## Part F — Next: TillPOS plan Task 1 steps 4–7

On this copy, follow **Task 1, steps 4–7** of `docs/superpowers/plans/2026-10-05-tillpos-plan1-engine-and-catalog-sync.md`, using **http://localhost:8000** wherever the plan says `http://localhost:8080`:

- Step 4: Allow Negative Stock; role **TillPOS Device** with the listed permissions; user `till1@shop.local` with API key/secret; POS Profile for Till 1 (write-off limit 0.05).
- Steps 5–6: run the API checks and fill in `docs/erp-api-notes.md`, plus the extra checks in `docs/superpowers/plans/2026-10-05-tillpos-plan1-followups.md` §1.

---

## Daily use

```bash
cd ~/frappe-bench && bench start        # Ctrl+C to stop
```

**Refresh with newer live data:** repeat A2–A3, copy to `~/backup`, then run the Part E commands from `set-config mute_emails` onwards, and redo "Cut the copy off".
**Free the RAM when not testing:** close `bench start`, then in PowerShell `wsl --shutdown`.
