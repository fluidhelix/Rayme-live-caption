"""Actual Chromium + authenticated local WebSocket controls; no audio/model."""
import os,subprocess
from pathlib import Path
from playwright.sync_api import sync_playwright,expect
root=Path(__file__).resolve().parents[1]
fixture=r'''
const {createUI}=require('./windows-captions/server');
(async()=>{let ui,commands=0,revision=1;const apps=Array.from({length:5},(_,i)=>({kind:'process',id:(10+i)+':100',pid:10+i,started:'100',name:'App '+i}));
ui=await createUI({port:0,onRefresh:()=>{ui.publish({type:'applications',supported:true,apps:[...apps.slice(1),{kind:'process',id:'99:999',pid:99,started:'999',name:'App 0'}]});ui.publish({type:'status',message:'Refreshed; configured='+commands});},onConfigure:(slot,d)=>{commands++;if(d)ui.publish({type:'source-status',source:{id:slot,generation:revision++,name:d.name,identity:d.id},state:'waiting',message:'Synthetic selection'});ui.publish({type:'status',message:'Configured '+commands});}});ui.publish({type:'applications',supported:true,apps});console.log(ui.url);})();
'''
child=subprocess.Popen(['node','-e',fixture],cwd=root,stdout=subprocess.PIPE,text=True)
try:
    url=child.stdout.readline().strip()
    with sync_playwright() as p:
        args={'headless':True}
        if os.environ.get('CHROMIUM_PATH'):args['executable_path']=os.environ['CHROMIUM_PATH']
        elif Path('/usr/bin/chromium').exists():args['executable_path']='/usr/bin/chromium'
        browser=p.chromium.launch(**args);page=browser.new_page();page.goto(url)
        expect(page.locator('#row-a')).to_be_visible();expect(page.locator('#row-b')).to_be_hidden()
        for slot in ['b','c','d','e']:
            page.locator('#add-app').click();expect(page.locator('#row-'+slot)).to_be_visible()
        expect(page.locator('#add-app')).to_be_hidden()
        page.get_by_role('button',name='Remove Application C',exact=True).click()
        expect(page.locator('#row-c')).to_be_hidden();expect(page.locator('#row-d')).to_be_visible()
        page.locator('#add-app').click();expect(page.locator('#row-c')).to_be_visible()
        # Choose an existing identity explicitly; refresh replaces discovery only.
        page.locator('#app-a').select_option('10:100');page.locator('#enable-a').check()
        expect(page.locator('#status')).to_have_text('Configured 1')
        page.locator('#refresh').click();expect(page.locator('#status')).to_have_text('Refreshed; configured=1')
        expect(page.locator('#app-a')).to_have_value('10:100')
        assert page.evaluate('sources.a.source.identity')=='10:100'
        expect(page.locator('#app-a option[value="99:999"]')).to_have_count(1)
        page.locator('#app-a').select_option('99:999');expect(page.locator('#status')).to_have_text('Configured 2')
        assert page.evaluate('sources.a.source.identity')=='99:999'
        browser.close()
    print('PASS five-source browser: progressive controls, optional removal, preserved selection and manual restarted-PID selection')
finally:
    child.terminate();child.wait(timeout=5)
