# E-Invoice Ops Runbook

This file defines the minimum sandbox checks and operational alarms for the e-invoice pipeline.

## 1) Sandbox E2E Checklist

Run these steps in order:

1. Create a sale with IBAN payment from the WPF app.
2. Confirm `Invoices.IsExported = 0` and a row exists in `EInvoiceDocuments` (`Status=Queued`).
3. Confirm a row exists in `EInvoiceOutboxes` (`Status=Pending`).
4. Wait for `EInvoiceOutboxWorker` (20s cycle) and re-check:
   - `EInvoiceDocuments.Status` becomes `Sent` or `Delivered`
   - `Invoices.IsExported = 1`
5. Call `GET /api/einvoice/outgoing` with the branch filter and verify item appears.
6. Call `POST /api/einvoice/outgoing/{invoiceId}/send` and verify a new outbox retry row is created.
7. Post a signed webhook payload to `POST /api/einvoice/webhook/stub-integrator` and verify:
   - `EInvoiceWebhookLogs.IsVerified = 1`
   - related `EInvoiceDocuments.Status` updates

## 2) Replay / Signature Controls

- Webhooks require `X-Webhook-Signature` validation (`HMAC-SHA256`) in provider adapter verification.
- Duplicate webhook replay is ignored by `(TenantId, ProviderCode, EventId)` check and unique index in `EInvoiceWebhookLogs`.
- Every webhook is audit-logged in `EInvoiceWebhookLogs`.

## 3) Alarm Thresholds

Poll `GET /api/einvoice/ops/health` and alert when:

- `deadLetterOutbox > 0`
- `failedDocuments > 0`
- `delayedOutbox > 10`
- `invalidWebhooks > 0`

Recommended polling interval: 1 minute.

## 4) Recovery Actions

- For dead-letter or failed docs, use `POST /api/einvoice/outgoing/{invoiceId}/send`.
- For recurring invalid signatures, rotate `EInvoice:WebhookSecret` and update integrator webhook config.
- For provider outage, keep worker running; retry/backoff is automatic.
