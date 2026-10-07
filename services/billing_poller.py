"""Billing team: polls a payment provider for settled invoices."""

from __future__ import annotations

import random
import time

from company_tracing import job_run, traced


@traced(record_args=["batch_size"])
def fetch_settled_invoices(batch_size: int) -> list[str]:
    time.sleep(0.01)
    return [f"inv-{random.randint(1000, 9999)}" for _ in range(batch_size)]


@traced(record_args=["invoice_id"])
def mark_paid(invoice_id: str) -> None:
    time.sleep(0.002)
    if invoice_id.endswith("7"):
        raise RuntimeError(f"ledger rejected {invoice_id}")


@job_run("billing.settle_invoices", swallow_exceptions=True)
def settle_invoices_once() -> int:
    invoices = fetch_settled_invoices(batch_size=3)
    for invoice_id in invoices:
        mark_paid(invoice_id)
    return len(invoices)
