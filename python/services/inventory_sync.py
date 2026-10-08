"""Inventory team: async sync against a warehouse API."""

from __future__ import annotations

import asyncio

from company_tracing import job_run, traced


@traced(record_args=["warehouse"])
async def fetch_stock_levels(warehouse: str) -> dict[str, int]:
    await asyncio.sleep(0.01)
    return {"sku-1": 4, "sku-2": 0}


@traced
async def write_stock_levels(levels: dict[str, int]) -> None:
    await asyncio.sleep(0.005)


@job_run("inventory.sync", attributes={"inventory.region": "us-east"})
async def sync_once() -> None:
    levels = await fetch_stock_levels(warehouse="wh-7")
    await write_stock_levels(levels)
