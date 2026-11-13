"""Synthetic two-source integration. Python Playwright; no capture/model/token needed."""
import os
import subprocess
from pathlib import Path
from playwright.sync_api import sync_playwright, expect
root = Path(__file__).resolve().parents[1]
fixture = r"""
const {createUI}=require('./windows-captions/server');
(async()=>{let ui,phase=0;const sources={a:{id:'a',name:'Chrome',identity:'10:100',generation:1},b:{id:'b',name:'Discord',identity:'20:200',generation:2}};
ui=await createUI({port:0,onRefresh:()=>{
phase++;
const c=(source,id,revision,final,text)=>ui.publish({type:'caption',source,id,revision,final,text,capturedAt:id});
const retire=(source,id,revision,terminal,retain=false)=>ui.publish({type:'segment-retired',source,id,revision,terminal,retain});
if(phase===1){sources.a={...sources.a,generation:3};ui.publish({type:'reset',source:sources.a});ui.publish({type:'source-status',source:sources.a,component:'capture',state:'waiting',message:'Listening',recovered:true});c(sources.a,40,1,false,'A unfinished');c(sources.b,40,1,false,'B unfinished');ui.publish({type:'source-status',source:sources.a,component:'inference',state:'unavailable',message:'Local transcription failed.'});retire(sources.a,40,2,false);c(sources.a,40,1,false,'late obsolete');c(sources.b,40,2,true,'B final *sighing*');}
if(phase===2){ui.publish({type:'source-status',source:sources.a,component:'inference',state:'recovering',message:'Retrying current speech.'});c(sources.a,40,3,false,'Recovered draft');retire(sources.a,40,2,false);c(sources.a,41,4,false,'Newer live text');retire(sources.a,40,2,true);c(sources.a,40,100,false,'late retired');}
if(phase===3){c(sources.a,41,5,true,'A recovered final [laughter]');ui.publish({type:'source-status',source:sources.a,component:'inference',state:'capturing',message:'Transcription ready.',recovered:true});retire(sources.a,41,6,true);}
if(phase===4){const old=sources.a;ui.publish({type:'source-removed',source:old});sources.a={...old,generation:4,identity:'11:101',name:'New application'};ui.publish({type:'reset',source:sources.a});c(old,42,100,false,'wrong application');c(sources.a,1,1,false,'New application live');}
if(phase===5)retire(sources.a,1,1,false,true);
if(phase===6)c(sources.a,1,2,true,'New application verified final');
if(phase===7){for(let id=2;id<=215;id++)c(sources.a,id,id,true,'Retained browser sentence '+id);for(let id=41;id<=255;id++)c(sources.b,id,id,true,'Retained voice sentence '+id);}
},onConfigure:(slot,d)=>ui.publish(d?{type:'source-status',source:sources[slot],state:'capturing',message:'Synthetic capture'}:{type:'source-removed',source:sources[slot]}),onPause:paused=>ui.publish({type:'paused',paused})});
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
        assert page.locator('#history p').count() == 35
        assert 'Synthetic sentence 1' in page.locator('#history p').first.inner_text()
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
        assert page.locator('#history p').count() == 35
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
        # Live retirement, recovery and replacement use the real production server.
        page.locator('#refresh').click()
        expect(page.locator('#partials p')).to_have_count(0)
        expect(page.locator('#history p')).to_have_count(1)
        expect(page.locator('#source-status')).to_contain_text('Local transcription failed.')
        page.reload()
        expect(page.locator('#source-status')).to_contain_text('Local transcription failed.')
        expect(page.locator('#partials p')).to_have_count(0)
        page.locator('#refresh').click()
        expect(page.locator('#partials p[data-source="a"]')).to_contain_text('Newer live text')
        expect(second.locator('#partials p[data-source="a"]')).to_contain_text('Newer live text')
        expect(page.locator('#source-status')).to_contain_text('recovering')
        expect(page.locator('#source-status')).to_contain_text('Local transcription failed.')
        page.locator('#refresh').click()
        expect(page.locator('#history p')).to_have_count(2)
        expect(page.locator('#partials p')).to_have_count(0)
        expect(page.locator('#source-status')).not_to_contain_text('failed')
        assert '[laughter]' in page.locator('#history').inner_text()
        page.locator('#refresh').click()
        expect(page.locator('#partials p')).to_have_count(1)
        expect(page.locator('#partials p strong')).to_have_text('New application')
        assert 'wrong application' not in page.locator('main').inner_text()
        assert 'A recovered final' not in page.locator('#history').inner_text()
        assert 'B final *sighing*' in page.locator('#history').inner_text()
        page.reload()
        expect(page.locator('#partials p')).to_contain_text('New application live')
        page.locator('#refresh').click()
        expect(page.locator('#partials p')).to_have_count(0)
        expect(page.locator('#history p[data-unfinished="true"]')).to_contain_text('New application live')
        expect(second.locator('#history p[data-unfinished="true"]')).to_contain_text('New application live')
        page.reload()
        expect(page.locator('#history p[data-unfinished="true"]')).to_have_count(1)
        page.locator('#refresh').click()
        expect(page.locator('#history p[data-unfinished="true"]')).to_have_count(0)
        expect(page.locator('#history p[data-source="a"]')).to_have_count(1)
        expect(page.locator('#history')).to_contain_text('New application verified final')
        page.locator('#refresh').click()
        expect(page.locator('#history p[data-source="a"]')).to_have_count(200)
        expect(page.locator('#history p[data-source="b"]')).to_have_count(200)
        page.reload()
        expect(page.locator('#history p')).to_have_count(400)
        assert not errors, errors
        browser.close()
    print('PASS browser: independent 200-segment retention, unfinished/final replacement, reconnect, font, pause/resume, shared clear, source isolation, retirement revisions and persistent failure/recovery')
finally:
    child.terminate()
    child.wait(timeout=5)
