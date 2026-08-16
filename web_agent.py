import asyncio
from playwright.async_api import async_playwright

async def run_agent(url):
    async with async_playwright() as p:
        browser = await p.chromium.launch()
        page = await browser.new_page()
        await page.goto(url)
        print(f"Page Title: {await page.title()}")
        await browser.close()

if __name__ == "__main__":
    asyncio.run(run_agent("https://example.com"))