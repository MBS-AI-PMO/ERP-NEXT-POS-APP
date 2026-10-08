// Creates the ERPNext metadata TillPOS needs (idempotent). Run by an admin:
//   node erpnext-setup.mjs <config.json>
// config.json: { "BaseUrl": "https://…/", "ApiKey": "…", "ApiSecret": "…" }  (the user needs System Manager)
// It never touches transactions. It creates (only when missing) the Custom Fields, the "TillPOS Approval" and "POS Cashier"
// DocTypes, the optional cashiers from the config and the "Rounding" Mode of Payment, and adds the till role's permission rows on POS Cashier and TillPOS
// Approval (existing permission rows are kept exactly as they are; rows are only appended). Then it reports, read-only, what is
// still to be done by hand.
import { readFileSync } from 'node:fs';

const cfg = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const base = cfg.BaseUrl.replace(/\/$/, '');
const headers = { Authorization: `token ${cfg.ApiKey}:${cfg.ApiSecret}`, Accept: 'application/json', 'Content-Type': 'application/json' };

async function call(method, path, body) {
  const res = await fetch(base + path, { method, headers, body: body ? JSON.stringify(body) : undefined });
  const text = await res.text();
  let json; try { json = JSON.parse(text); } catch { json = { raw: text.slice(0, 300) }; }
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status}: ${json._error_message ?? json.exception ?? json.raw ?? text.slice(0, 300)}`);
  return json;
}
const exists = async (doctype, name) => {
  const r = await fetch(`${base}/api/resource/${encodeURIComponent(doctype)}/${encodeURIComponent(name)}`, { headers });
  return r.status === 200;
};

const customFields = [
  { dt: 'POS Invoice', fieldname: 'custom_till', label: 'Till', fieldtype: 'Data', read_only: 1, insert_after: 'posa_client_request_id', description: 'TillPOS till number, e.g. TILL1' },
  { dt: 'POS Opening Shift', fieldname: 'custom_offline_id', label: 'Offline ID', fieldtype: 'Data', read_only: 1, unique: 1, insert_after: 'pos_profile', description: 'TillPOS shift id' },
  { dt: 'POS Closing Shift', fieldname: 'custom_offline_id', label: 'Offline ID', fieldtype: 'Data', read_only: 1, unique: 1, insert_after: 'pos_profile', description: 'TillPOS shift id' },
];

for (const f of customFields) {
  const name = `${f.dt}-${f.fieldname}`;
  if (await exists('Custom Field', name)) { console.log(`ok   Custom Field ${name} exists`); continue; }
  await call('POST', '/api/resource/Custom Field', { doctype: 'Custom Field', ...f });
  console.log(`made Custom Field ${name}`);
}

const approvalDoctype = {
  doctype: 'DocType', name: 'TillPOS Approval', module: 'Selling', custom: 1, is_submittable: 0,
  autoname: 'TPA-.#####', title_field: 'action', track_changes: 1,
  description: 'Supervisor approvals and failed PIN attempts recorded by TillPOS tills',
  fields: [
    { fieldname: 'action', label: 'Action', fieldtype: 'Data', reqd: 1, in_list_view: 1 },
    { fieldname: 'cashier', label: 'Cashier', fieldtype: 'Data', in_list_view: 1 },
    { fieldname: 'supervisor', label: 'Supervisor', fieldtype: 'Data', in_list_view: 1 },
    { fieldname: 'shift', label: 'Shift', fieldtype: 'Link', options: 'POS Opening Shift' },
    { fieldname: 'invoice', label: 'Invoice', fieldtype: 'Link', options: 'POS Invoice' },
    { fieldname: 'item_code', label: 'Item', fieldtype: 'Link', options: 'Item' },
    { fieldname: 'amount', label: 'Amount', fieldtype: 'Currency', in_list_view: 1 },
    { fieldname: 'reason', label: 'Reason', fieldtype: 'Small Text' },
    { fieldname: 'at', label: 'At', fieldtype: 'Datetime', in_list_view: 1 },
    { fieldname: 'till', label: 'Till', fieldtype: 'Data' },
    { fieldname: 'custom_offline_id', label: 'Offline ID', fieldtype: 'Data', unique: 1, read_only: 1 },
  ],
  permissions: [
    { role: 'System Manager', read: 1, write: 1, create: 1, delete: 1, report: 1, export: 1 },
    { role: 'Accounts Manager', read: 1, report: 1, export: 1 },
  ],
};

if (await exists('DocType', 'TillPOS Approval')) console.log('ok   DocType TillPOS Approval exists');
else { await call('POST', '/api/resource/DocType', approvalDoctype); console.log('made DocType TillPOS Approval'); }

// POS Cashier: the till downloads this list (PIN at permission level 1 so ordinary users cannot read it).
const cashierDoctype = {
  doctype: 'DocType', name: 'POS Cashier', module: 'Selling', custom: 1, is_submittable: 0,
  autoname: 'field:cashier_name', title_field: 'cashier_name', track_changes: 1,
  description: 'Cashiers and supervisors who may log in to TillPOS tills',
  fields: [
    { fieldname: 'cashier_name', label: 'Cashier Name', fieldtype: 'Data', reqd: 1, unique: 1, in_list_view: 1 },
    { fieldname: 'user', label: 'ERPNext User', fieldtype: 'Link', options: 'User', in_list_view: 1 },
    { fieldname: 'pin', label: 'PIN', fieldtype: 'Data', permlevel: 1, description: '4-6 digits, unique across cashiers' },
    { fieldname: 'is_supervisor', label: 'Is Supervisor', fieldtype: 'Check', in_list_view: 1 },
    { fieldname: 'enabled', label: 'Enabled', fieldtype: 'Check', default: '1', in_list_view: 1 },
  ],
  permissions: [
    { role: 'System Manager', read: 1, write: 1, create: 1, delete: 1, report: 1, export: 1 },
    { role: 'System Manager', permlevel: 1, read: 1, write: 1 },
  ],
};
if (await exists('DocType', 'POS Cashier')) console.log('ok   DocType POS Cashier exists');
else { await call('POST', '/api/resource/DocType', cashierDoctype); console.log('made DocType POS Cashier'); }

// Optional cashiers from the config: "Cashiers": [{ "cashier_name": "...", "pin": "1234", "is_supervisor": 0, "user": "..." }]
for (const c of cfg.Cashiers ?? []) {
  if (await exists('POS Cashier', c.cashier_name)) { console.log(`ok   POS Cashier ${c.cashier_name} exists`); continue; }
  await call('POST', '/api/resource/POS Cashier', { doctype: 'POS Cashier', enabled: 1, ...c });
  console.log(`made POS Cashier ${c.cashier_name}`);
}

// The till role needs: POS Cashier read (incl. the PIN at permission level 1), TillPOS Approval read + create.
const tillRole = cfg.TillRole ?? 'TillPOS Device';
async function ensurePerms(doctype, rows) {
  const dt = (await call('GET', '/api/resource/DocType/' + encodeURIComponent(doctype))).data;
  // Every existing row is sent back whole (all its fields: if_owner, print, email, share, amend, cancel, select, …, and its
  // name, so ERPNext keeps the row as it is); new rows are only appended.
  const perms = dt.permissions.map(row => ({ ...row }));
  let changed = false;
  for (const want of rows) {
    if (perms.some(x => x.role === want.role && (x.permlevel ?? 0) === (want.permlevel ?? 0))) continue;
    perms.push(want); changed = true;
  }
  if (!changed) { console.log(`ok   ${doctype} permissions for ${tillRole}`); return; }
  await call('PUT', '/api/resource/DocType/' + encodeURIComponent(doctype), { permissions: perms });
  console.log(`made ${doctype} permissions for ${tillRole}`);
}
if (await exists('Role', tillRole)) {
  await ensurePerms('POS Cashier', [{ role: tillRole, permlevel: 0, read: 1 }, { role: tillRole, permlevel: 1, read: 1 }]);
  await ensurePerms('TillPOS Approval', [{ role: tillRole, permlevel: 0, read: 1, create: 1 }]);
} else console.log(`!!   Role ${tillRole} does not exist — create it (see docs/erpnext-production-setup.md step 2)`);

// The "Rounding" Mode of Payment: an exact card bill whose rounded total is a few fils higher pays those fils in a Rounding row
// (ERPNext refuses a POS Invoice paid below its rounded total), booked to each company's round-off account.
if (await exists('Mode of Payment', 'Rounding')) console.log('ok   Mode of Payment Rounding exists');
else {
  const companies = (await call('GET', '/api/resource/Company?fields=["name","round_off_account"]')).data;
  const missing = companies.filter(c => !c.round_off_account).map(c => c.name);
  if (missing.length) console.log(`!!   Company ${missing.join(', ')} has no Round Off Account — set it, then run this again`);
  else {
    await call('POST', '/api/resource/Mode of Payment', { mode_of_payment: 'Rounding', type: 'General', enabled: 1,
      accounts: companies.map(c => ({ company: c.name, default_account: c.round_off_account })) });
    console.log('made Mode of Payment Rounding');
  }
}

// Report, read-only: things the admin still has to do by hand.
const profiles = await call('GET', '/api/resource/POS Profile?fields=["name","disable_rounded_total","write_off_limit","write_off_account","account_for_change_amount","customer"]');
console.log('POS Profiles:', JSON.stringify(profiles.data));
const company = await call('GET', '/api/resource/Company?fields=["name","tax_id"]');
console.log('Company tax_id:', JSON.stringify(company.data));
console.log('Done. Still manual if missing: Company Tax ID (TRN), company address on each POS Profile, POS Cashier doctype + cashiers, Allow Negative Stock, TillPOS Device role for till users.');
