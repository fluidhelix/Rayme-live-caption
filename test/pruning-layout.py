"""Local Chromium geometry experiment, NOT native WPF validation.
Reproduces prefix reflow and tests the bounded continuation-indent strategy.
"""
import os
from pathlib import Path
from playwright.sync_api import sync_playwright
with sync_playwright() as p:
    options={'headless':True}
    if os.environ.get('CHROMIUM_PATH'): options['executable_path']=os.environ['CHROMIUM_PATH']
    elif Path('/usr/bin/chromium').exists(): options['executable_path']='/usr/bin/chromium'
    browser=p.chromium.launch(**options)
    page=browser.new_page()
    result=page.evaluate('''() => {
      let moved=0, stable=0;
      const words=['yes','extraordinarily','maybe','I','comfortable','wide WMW','thin iii','conversation','again'];
      for(const width of [300,420,670]) for(const size of [16,26,40]) {
        for(const protect of [false,true]) {
          const view=document.createElement('div');view.style.cssText=`width:${width}px;height:180px;overflow:auto;overflow-anchor:none;scrollbar-width:none;font:${size}px Arial;line-height:1.4`;
          const para=document.createElement('p');para.style.margin='0';view.append(para);document.body.append(view);
          for(let i=0;i<200;i++){const span=document.createElement('span');span.textContent=(i?' ':'')+Array.from({length:2+i%5},(_,j)=>words[(i+j)%words.length]).join(' ');para.append(span);}
          function rect(node,index){const r=document.createRange();r.setStart(node.firstChild,index);r.setEnd(node.firstChild,index+1);return r.getBoundingClientRect();}
          for(let pass=0;pass<30;pass++){
            const anchor=para.children[100], neighbor=para.children[101];view.scrollTop+=rect(anchor,2).y-view.getBoundingClientRect().y-25;
            const before=rect(anchor,2), oldNeighbor=rect(neighbor,2), head=para.children[1];
            const first=rect(para.firstChild,0), leading=head.textContent.startsWith(' ')?1:0;
            const indent=rect(head,leading).x-(first.x-parseFloat(para.style.textIndent||0));
            if(protect)para.style.textIndent=Math.max(0,Math.min(width,indent))+'px';
            para.firstChild.remove();head.textContent=head.textContent.slice(leading);
            const after=rect(anchor,2), next=rect(neighbor,2);
            if(protect){
              if(Math.abs(after.x-before.x)>1 || Math.abs(next.x-oldNeighbor.x)>1 || Math.abs((next.y-after.y)-(oldNeighbor.y-before.y))>1)throw Error(JSON.stringify({width,size,pass,before:before.toJSON(),after:after.toJSON(),indent}));
              stable++;
            }else if(Math.abs(after.x-before.x)>1)moved++;
            if(para.children.length!==199-pass)throw Error('Pruned nodes retained');
          }
          view.remove();
        }
      }
      if(!moved)throw Error('Baseline reflow not reproduced');return {baselineHorizontalMovements:moved,stablePrunes:stable,widths:3,fontSizes:3};
    }''')
    print('PASS Chromium continuation geometry experiment (not WPF):',result)
    browser.close()
