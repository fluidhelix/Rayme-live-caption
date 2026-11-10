"""Synthetic two-source integration. Python Playwright; no capture/model/token needed."""
import os
import subprocess
from pathlib import Path
from playwright.sync_api import sync_playwright, expect
root = Path(__file__).resolve().parents[1]
fixture = r"""
const {createUI}=require('./windows-captions/server');
(async()=>{let ui;const sources={a:{id:'a',name:'Chrome',identity:'10:100',generation:1},b:{id:'b',name:'Discord',identity:'20:200',generation:2}};
ui=await createUI({port:0,onRefresh:()=>{},onConfigure:(slot,d)=>ui.publish(d?{type:'source-status',source:sources[slot],state:'capturing',message:'Synthetic capture'}:{type:'source-removed',source:sources[slot]}),onPause:paused=>ui.publish({type:'paused',paused})});
ui.publish({type:'applications',supported:true,apps:[{id:'10:100',pid:10,started:'100',name:'Chrome',kind:'process'},{id:'20:200',pid:20,started:'200',name:'Discord',kind:'process'}]});
for(const source of Object.values(sources))ui.publish({type:'source-status',source,state:'capturing',message:'Synthetic capture'});
for(let id=1;id<=35;id++)ui.publish({type:'caption',source:sources[id%2?'a':'b'],id,text:'Synthetic sentence '+id,final:true,capturedAt:id});
for(const source of Object.values(sources))ui.publish({type:'caption',source,id:36,text:source.name+' *laughter*',final:false});
console.log(ui.url);})();
"""
child = subprocess.Popen(['node', '-e', fixture], cwd=root, stdout=subprocess.PIPE, text=True)
try:
    url = child.stdout.readline().strip()
    with sync_playwright() as p:
        args = {'headless': True}
        executable = os.environ.get('CHROMIUM_PATH')
        if executable:
            args['executable_path'] = executable
        elif Path('/usr/bin/chromium').exists():
            args['executable_path'] = '/usr/bin/chromium'
        browser = p.chromium.launch(**args)
        page = browser.new_page()
        errors = []
        page.on('pageerror', lambda e: errors.append(str(e)))
        page.goto(url)
        page.locator('#partials p').filter(has_text='Chrome *laughter*').wait_for()
        assert page.locator('#partials p').count() == 2
        assert page.locator('#partials p[data-source="a"] strong').inner_text() == 'Chrome'
        assert page.locator('#partials p[data-source="b"] strong').inner_text() == 'Discord'
        assert page.locator('#history p').count() == 30
        assert 'Synthetic sentence 6' in page.locator('#history p').first.inner_text()
        page.locator('summary').click()
        page.locator('#font').fill('36')
        page.locator('#font').dispatch_event('input')
        assert page.locator('main').evaluate('(el)=>getComputedStyle(el).fontSize') == '36px'
        page.locator('#pause').click()
        page.get_by_role('button', name='Resume capture').wait_for()
        page.locator('#pause').click()
        page.get_by_role('button', name='Pause capture').wait_for()
        page.reload()
        page.locator('#partials p').filter(has_text='Discord *laughter*').wait_for()
        assert page.locator('#history p').count() == 30
        # Every connected display must clear, and cleared captions must stay gone after reload.
        second = browser.new_page(); second.goto(url)
        second.locator('#history p').first.wait_for()
        page.locator('summary').click()
        page.locator('#clear').click()
        expect(page.locator('#history p')).to_have_count(0)
        expect(second.locator('#history p')).to_have_count(0)
        page.reload()
        expect(page.locator('#enable-b')).to_be_checked()
        assert page.locator('#history p').count() == 0
        assert page.locator('#partials p').count() == 0
        page.locator('#enable-a').uncheck()
        expect(page.locator('#source-status div').filter(has_text='Chrome')).to_have_count(0)
        assert page.locator('#enable-b').is_checked()
        assert not errors, errors
        browser.close()
    print('PASS browser: two labels/partials, 30 entries, reconnect, font, pause/resume, server clear across displays/reload, source disable')
finally:
    child.terminate()
    child.wait(timeout=5)
