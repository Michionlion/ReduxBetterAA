"""Acceptance for the immutable Test view; candidate failures remain evidence."""
import argparse
import json
from pathlib import Path
import numpy as np

FIXTURE_HASH='C0E289A1F7A413D45C48AD5A9FD7A66118252EF01E8CCFC46E744F5136B5D751'


def verify(root):
    run=json.loads((root/'run.json').read_text(encoding='utf-8-sig'))
    results={r['label']:r for r in json.loads((root/'terrain-analysis.json').read_text())}
    checks=[]
    def check(condition, message): checks.append({'passed':bool(condition),'check':message})
    check(run['fixtureSha256'].get('local/terrain-test-launchpad4')==FIXTURE_HASH,'Immutable Test quicksave')
    check(run['exitCode']==0 and run['settingsRestored'] and run['modUnchanged'],'Harness passed and state restored')
    comparisons={}
    for name in ('NVIDIADLAA','TAA','FSR2NativeAA'):
        before,after=results[name+'-before'],results[name+'-after']
        b=before['metrics']['output']['hill_blocks']['16']['mae']
        a=after['metrics']['output']['hill_blocks']['16']['mae']
        check(before['stationary_score_applicable'] and after['stationary_score_applicable'],'Correct stationary view: '+name)
        check(before['classification']=='coherent_hillside_flicker','Bypass reproduces flicker: '+name)
        check(after['classification']=='within_terrain_flicker_limit' and a/b<.1,'At least 90% reduction below absolute limit: '+name)
        check(after['count']>=128 and after['pqs_depth_current_frame_fraction']==1 and after['pqs_depth_projection_max_difference']<1e-7,'Every production frame has matching terrain depth: '+name)
        comparisons[name]={'before':b,'after':a,'reduction_percent':100*(1-a/b)}
        for suffix in ('pan','launch'):
            r=results[name+'-'+suffix]
            check(r['pqs_depth_current_frame_fraction']==1 and r['pqs_depth_projection_max_difference']<1e-7,'Matching depth during '+suffix+': '+name)
            check(not r['stationary_score_applicable'],'Moving case excluded from still-view threshold: '+name+' '+suffix)
            if suffix=='launch': check(r['speed_range'][1]>1,'Vessel really launched: '+name)
    for name in ('Off-before','Off-after'):
        r=results[name]
        check(r['classification']=='within_terrain_flicker_limit' and r['input_output_mae']==0,'Off is quiet and unfiltered: '+name)
    profiles={}
    for name in comparisons:
        groups={}
        for candidate in ('production-bypass','none'):
            paths=list((root/'samples').glob(name+'-*-'+candidate+'-profile.json'))
            check(len(paths)==2,'Two timing windows: '+name+' '+candidate)
            frames=[f for p in paths for f in json.loads(p.read_text())['frames']]
            check(len(frames)==720,'720 measured draws: '+name+' '+candidate)
            groups[candidate]={'draw_ms_median':float(np.median([f['ms'] for f in frames])),
                'draw_ms_p95':float(np.percentile([f['ms'] for f in frames],95)),
                'frame_ms_mean':float(np.mean([f['deltaMs'] for f in frames])),
                'frame_ms_p95':float(np.percentile([f['deltaMs'] for f in frames],95)),
                'allocation_bytes_median':float(np.median([f['allocated'] for f in frames])),
                'allocation_bytes_max':max(f['allocated'] for f in frames)}
        check(groups['none']['allocation_bytes_max']==groups['production-bypass']['allocation_bytes_max'],'No added allocation in timed draw: '+name)
        check(groups['none']['draw_ms_median']-groups['production-bypass']['draw_ms_median']<.05,'Added median CPU draw cost below 0.05 ms: '+name)
        profiles[name]=groups
    report={'passed':all(c['passed'] for c in checks),'checks':checks,'comparisons':comparisons,'profiles':profiles,
            'limit':.00025,'note':'Threshold only for immutable Test view; moving captures need visual review. CPU timing includes PQS draw and test gate; GPU timing unavailable.'}
    (root/'terrain-verification.json').write_text(json.dumps(report,indent=2))
    for c in checks:
        if not c['passed']: print('FAIL',c['check'])
    print('PASS' if report['passed'] else 'FAIL',len(checks),'terrain acceptance checks')
    return report['passed']


if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('run',type=Path)
    raise SystemExit(0 if verify(parser.parse_args().run) else 1)
