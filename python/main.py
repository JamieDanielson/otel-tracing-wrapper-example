"""Run all three example services in one process with console export."""

from __future__ import annotations

import asyncio

import telemetry
from company_tracing import poll_forever
from services import billing_poller, inventory_sync, notification_sender


def main() -> None:
    provider = telemetry.configure("tracing-wrapper-example")
    try:
        poll_forever(
            billing_poller.settle_invoices_once,
            interval_seconds=0.05,
            max_runs=2,
        )
        asyncio.run(inventory_sync.sync_once())
        notification_sender.drain_once()
    finally:
        provider.shutdown()


if __name__ == "__main__":
    main()
