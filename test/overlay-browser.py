"""Optional developer UI integration: Python Playwright + installed Chromium required.
Run from the repository root: python test/overlay-browser.py
Uses synthetic captions only; never starts Whisper/Discord or captures audio.
"""
import json
import subprocess
from pathlib import Path
from playwright.sync_api import sync_playwright

root = Path(__file__).resolve().parents[1]
fixture = r"""
const {createUI}=require('./windows-captions/server');
(async()=>{const ui=await createUI({port:0,onSelect:async()=>{},onRefresh:async()=>{}});
for(let id=1;id<=35;id++)ui.publish({type:'caption',id,text:'Synthetic sentence '+id,final:true});
ui.publish({type:'caption',id:36,text:'*laughter*',final:false});console.log(ui.url);})();
"""
child = subprocess.Popen(['node', '-e', fixture], cwd=root, stdout=subprocess.PIPE, text=True)
try:
    url = child.stdout.readline().strip()
    with sync_playwright() as p:
        import os
        browser = p.chromium.launch(executable_path=os.environ.get('CHROMIUM_PATH', '/usr/bin/chromium'), headless=True)
        page = browser.new_page()
        errors = []
        page.on('pageerror', lambda e: errors.append(str(e)))
        page.goto(url)
        page.locator('#partial').filter(has_text='*laughter*').wait_for()
        assert page.locator('#history p').count() == 30
        assert page.locator('#history p').first.inner_text() == 'Synthetic sentence 6'
        page.locator('summary').click()
        page.locator('#font').fill('36')
        page.locator('#font').dispatch_event('input')
        assert page.locator('main').evaluate('(el)=>getComputedStyle(el).fontSize') == '36px'
        page.reload()
        page.locator('#partial').filter(has_text='*laughter*').wait_for()
        assert page.locator('#history p').count() == 30
        page.locator('summary').click()
        page.locator('#clear').click()
        assert page.locator('#history p').count() == 0
        assert not errors, errors
        browser.close()
    print('PASS Chromium integration: real WebSocket, history/reconnect, vocal cue, font size, clear')
finally:
    child.terminate()
    child.wait(timeout=5)
