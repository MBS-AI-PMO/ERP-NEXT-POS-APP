namespace TillPOS.Data;

/// <summary>Schema versions; index i upgrades user_version i → i+1. Never edit a shipped entry — append.</summary>
internal static class Migrations
{
    public static readonly string[] All =
    [
        """
        CREATE TABLE item (
            id INTEGER PRIMARY KEY,
            item_code TEXT NOT NULL UNIQUE,
            item_name TEXT NOT NULL,
            item_group TEXT NOT NULL,
            brand TEXT,
            stock_uom TEXT NOT NULL,
            disabled INTEGER NOT NULL,
            is_sales_item INTEGER NOT NULL);
        CREATE VIRTUAL TABLE item_fts USING fts5(item_name, tokenize = 'unicode61 remove_diacritics 2');
        CREATE TABLE item_barcode (barcode TEXT PRIMARY KEY, item_code TEXT NOT NULL, uom TEXT);
        CREATE INDEX ix_item_barcode_item ON item_barcode(item_code);
        CREATE TABLE item_uom (item_code TEXT NOT NULL, uom TEXT NOT NULL, conversion_factor TEXT NOT NULL, PRIMARY KEY (item_code, uom));
        CREATE TABLE item_tax (
            parent_type TEXT NOT NULL, parent TEXT NOT NULL, idx INTEGER NOT NULL,
            item_tax_template TEXT NOT NULL, tax_category TEXT, valid_from TEXT,
            PRIMARY KEY (parent_type, parent, idx));
        CREATE TABLE item_price (
            name TEXT PRIMARY KEY, item_code TEXT NOT NULL, uom TEXT,
            price_list_rate TEXT NOT NULL, valid_from TEXT, valid_upto TEXT);
        CREATE INDEX ix_item_price_item ON item_price(item_code);
        CREATE TABLE item_group (name TEXT PRIMARY KEY, parent TEXT, lft INTEGER NOT NULL, rgt INTEGER NOT NULL);
        CREATE TABLE pricing_rule (name TEXT PRIMARY KEY, json TEXT NOT NULL);
        CREATE TABLE item_tax_template (name TEXT PRIMARY KEY, json TEXT NOT NULL);
        CREATE TABLE sales_tax_template (name TEXT PRIMARY KEY, json TEXT NOT NULL);
        CREATE TABLE kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """,
        """
        CREATE TABLE receipt (
            client_id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            return_against TEXT,
            shift_client_id TEXT NOT NULL,
            created_at TEXT NOT NULL,
            json TEXT NOT NULL,
            sync_status TEXT NOT NULL DEFAULT 'Pending',
            erp_name TEXT,
            last_error TEXT,
            attempts INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX ix_receipt_status ON receipt(sync_status, created_at);
        CREATE INDEX ix_receipt_shift ON receipt(shift_client_id);
        CREATE INDEX ix_receipt_return_against ON receipt(return_against);
        """,
        """
        CREATE TABLE held_cart (id TEXT PRIMARY KEY, label TEXT NOT NULL, held_at TEXT NOT NULL, json TEXT NOT NULL);
        """,
        """
        CREATE TABLE shift (
            client_id TEXT PRIMARY KEY,
            opened_at TEXT NOT NULL,
            closed_at TEXT,
            opening_json TEXT NOT NULL,
            closing_json TEXT,
            sync_status TEXT NOT NULL DEFAULT 'Pending',
            erp_opening TEXT,
            erp_closing TEXT,
            last_error TEXT);
        """,
        """
        CREATE TABLE cashier (id TEXT PRIMARY KEY, json TEXT NOT NULL);
        CREATE TABLE approval_log (id TEXT PRIMARY KEY, at TEXT NOT NULL, json TEXT NOT NULL, synced INTEGER NOT NULL DEFAULT 0);
        """,
        // Plan 2b upload state. A shift uploads two documents (opening, closing), each with its own status; their ERPNext
        // names go in the existing erp_opening / erp_closing columns. next_attempt_at (UTC, ISO 8601) is the per-document backoff.
        """
        ALTER TABLE receipt ADD COLUMN next_attempt_at TEXT;
        ALTER TABLE shift ADD COLUMN opening_status TEXT NOT NULL DEFAULT 'Pending';
        ALTER TABLE shift ADD COLUMN closing_status TEXT NOT NULL DEFAULT 'Pending';
        ALTER TABLE shift ADD COLUMN attempts INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE shift ADD COLUMN next_attempt_at TEXT;
        ALTER TABLE approval_log ADD COLUMN erp_name TEXT;
        ALTER TABLE approval_log ADD COLUMN sync_status TEXT NOT NULL DEFAULT 'Pending';
        ALTER TABLE approval_log ADD COLUMN last_error TEXT;
        ALTER TABLE approval_log ADD COLUMN attempts INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE approval_log ADD COLUMN next_attempt_at TEXT;
        UPDATE approval_log SET sync_status = 'Synced' WHERE synced = 1;
        """,
        // Plan 2b Task 5: recent POS Invoices of the other tills (read-only copies, for cross-till returns). posting is ERPNext's
        // posting date and time ("yyyy-MM-dd HH:mm:ss", shop time); json holds the whole invoice (lines, payments).
        """
        CREATE TABLE remote_receipt (
            erp_name TEXT PRIMARY KEY COLLATE NOCASE,
            client_request_id TEXT,
            till TEXT,
            pos_profile TEXT,
            posting TEXT NOT NULL,
            customer TEXT,
            grand_total TEXT NOT NULL,
            rounded_total TEXT NOT NULL,
            is_return INTEGER NOT NULL,
            return_against TEXT,
            json TEXT NOT NULL,
            fetched_at TEXT NOT NULL);
        CREATE INDEX ix_remote_receipt_client ON remote_receipt(client_request_id COLLATE NOCASE);
        CREATE INDEX ix_remote_receipt_return_against ON remote_receipt(return_against COLLATE NOCASE);
        CREATE INDEX ix_remote_receipt_posting ON remote_receipt(posting);
        """,
        // Plan 2b fix wave 2: unknown outcomes in a row per document (no answer to a write); after 3 the uploader escalates.
        """
        ALTER TABLE receipt ADD COLUMN unknown_attempts INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE shift ADD COLUMN unknown_attempts INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE approval_log ADD COLUMN unknown_attempts INTEGER NOT NULL DEFAULT 0;
        """,
    ];
}
