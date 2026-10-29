// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
// Synthetic contract tests only. These never create a Revit host report, run a
// DLL, or claim that their fixture data demonstrates Autodesk geometry behavior.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { parseCsv,openingUnion,frameworkRoundTrip,batchFingerprint,ruleFingerprint,verifyCompletion,REQUIRED_CASES } from './verify-civil-wall-phase3-host.mjs';

function completionFixture(){
  const runId='1'.repeat(32),launch={runId,processId:123,utc:'2026-01-01T00:00:00Z'},status={runId,phase:'completed',utc:'2026-01-01T00:00:05.010Z'};
  const report={schemaVersion:1,result:'PASS',runId,processId:123,finishedUtc:'2026-01-01T00:00:05Z',elapsedSeconds:4,sourceSaved:false,activeViewWritten:false,ribbonClickVerified:false,nativeWindowVerified:false,
    fixtureRollbackRequired:true,fixtureRolledBack:true,fixtureRollbackVerified:true,selectionRestored:true,activeViewUnchanged:true,sourceHashUnchanged:true,originalSourceHashUnchanged:true,revisionTrackingAttached:true,revisionTrackingDetached:true,requiredCasesPassed:true,liveRevit19WallSdkVerified:true,
    requiredCases:[...REQUIRED_CASES],blockedCases:[],cases:REQUIRED_CASES.map((name,index)=>({name,result:'PASS',batchId:index.toString(16).padStart(32,'0')})),fixtureCreatedUniqueIds:Array.from({length:8},(_,index)=>`synthetic-owned-id-${index}`),source:{elementCountBefore:10,isModifiedBefore:false},elementCountAfter:10,isModifiedAfter:false,revitYear:'2020',revitBuild:'synthetic contract only',expectedPackageVersion:'1.9.0',productionSourceCount:30,assertions:120};
  return{report,status,launch};
}
test('synthetic completion fixture validates the terminal contract only',()=>{const fixture=completionFixture();assert.equal(verifyCompletion(fixture.report,fixture.status,fixture.launch,Date.parse('2026-01-01T00:01:00Z')).phase,'completed');});
const badCompletion=[
  ['PARTIAL is not a host PASS',f=>{f.report.result='PARTIAL';}],
  ['waiting status is not completed',f=>{f.status.phase='waiting-for-exact-dedicated-model';}],
  ['wrong nonce',f=>{f.status.runId='2'.repeat(32);}],
  ['wrong process',f=>{f.report.processId++;}],
  ['late disconnected completion',f=>{f.status.utc='2026-01-02T00:00:00Z';}],
  ['finish before launch',f=>{f.report.finishedUtc='2025-12-31T23:59:59Z';}],
  ['negative elapsed',f=>{f.report.elapsedSeconds=-1;}],
  ['event handler left attached',f=>{f.report.revisionTrackingDetached=false;}],
  ['rollback missing',f=>{f.report.fixtureRolledBack=false;}],
  ['modified source bytes',f=>{f.report.sourceHashUnchanged=false;}],
  ['in-memory modification changed',f=>{f.report.isModifiedAfter=true;}],
  ['source element survived',f=>{f.report.elementCountAfter++;}],
  ['missing required window case',f=>{f.report.cases.pop();}],
  ['duplicate batch identities',f=>{f.report.cases[1].batchId=f.report.cases[0].batchId;}],
  ['blocked native family',f=>{f.report.blockedCases=[{case:'actual-window-cutout'}];}],
  ['stale live boolean cannot cover cleanup failure',f=>{f.report.trackingDetachError='synthetic failure';}],
  ['PASS cannot contain execution error',f=>{f.report.error='synthetic failure';}],
  ['wrong source version count',f=>{f.report.productionSourceCount=24;}],
  ['SDK is not ribbon validation',f=>{f.report.ribbonClickVerified=true;}]
];
for(const [label,mutate] of badCompletion)test(`synthetic rejection: ${label}`,()=>{const fixture=completionFixture();mutate(fixture);assert.throws(()=>verifyCompletion(fixture.report,fixture.status,fixture.launch,Date.parse('2026-01-01T00:01:00Z')));});
test('independent rectangle cells clip and avoid overlap double subtraction',()=>{assert.equal(openingUnion([{x0:1,x1:3,z0:.5,z1:1.5},{x0:2,x1:4,z0:.5,z1:1.5}],10,3),3);assert.equal(openingUnion([{x0:-1,x1:1,z0:-1,z1:1},{x0:11,x1:12,z0:0,z1:2}],10,3),1);assert.equal(openingUnion([{x0:-1,x1:11,z0:-1,z1:4}],10,3),30);});
test('strict CSV supports quoted commas, quotes, newlines, empty fields and CRLF',()=>{assert.deepEqual(parseCsv('"a,b","x""y","line1\nline2",\r\n"end",\r\n'),[['a,b','x"y','line1\nline2',''],['end','']]);});
for(const input of ['"unterminated','a"quote','"closed"trailing'])test(`strict CSV rejects ${JSON.stringify(input)}`,()=>assert.throws(()=>parseCsv(input)));
test('Framework R double text preserves binary facts rather than shortest JS text',()=>{assert.equal(frameworkRoundTrip(1e-8),'1E-08');assert.equal(frameworkRoundTrip(9.999999999999945),'9.9999999999999449');assert.equal(frameworkRoundTrip(2.9999999999999996),'2.9999999999999996');assert.equal(frameworkRoundTrip(-0),'0');assert.equal(frameworkRoundTrip(6),'6');assert.throws(()=>frameworkRoundTrip(NaN));});
test('typed metric digest matches explicit canonical string and detects type/value changes',()=>{
  const metric={Status:'available',Unit:'m',Source:'test',Value:9.999999999999945};
  const canonical='{CivilMetric;Source:S4:test;Status:S9:available;Unit:S1:m;Value:D9.9999999999999449;}';
  assert.equal(batchFingerprint(metric,'CivilMetric'),createHash('sha256').update(canonical,'utf8').digest('hex'));
  assert.notEqual(batchFingerprint({...metric,Value:10},'CivilMetric'),batchFingerprint(metric,'CivilMetric'));
  assert.throws(()=>batchFingerprint({...metric,Value:'10'},'CivilMetric'));
  assert.throws(()=>batchFingerprint({...metric,UnrecordedField:true},'CivilMetric'));
});
const defaultRule={SchemaVersion:1,TemplateId:'BV.Wall.ProjectGeometry',Name:'BV 项目几何核对',Description:'崛亮视觉 · Bright Vision 自有项目约定；独立矩形基准与实际实体核对，允许经证明的矩形实际切洞；不是地区清单或定额规则。',MethodId:'BV.Wall.RectSolid/1',AreaMethodId:'BV.Wall.AxisProjection/1',OpeningPolicy:'verified-rect-cutout',JoinPolicy:'reject-overlap',Revision:1,AbsoluteToleranceMetres3:1e-8,RelativeTolerance:1e-8,Priorities:[]};
test('new IEEE/length-prefixed rule fingerprint matches the shipped BV-owned contract',()=>{assert.equal(ruleFingerprint(defaultRule),'72632c65c0506db4cf9bfdfae6900e10960b84517f36068c8ddf48a763ff7332');assert.notEqual(ruleFingerprint({...defaultRule,Name:defaultRule.Name+' copied'}),ruleFingerprint(defaultRule));assert.notEqual(ruleFingerprint({...defaultRule,OpeningPolicy:'native-opening-only'}),ruleFingerprint(defaultRule));});
for(const edit of [{JoinPolicy:'implicit-priority'},{OpeningPolicy:'nominal-dimensions'},{AbsoluteToleranceMetres3:1},{Priorities:[{ElementUniqueId:'synthetic',Rank:1}]}])test(`rule rejects unsupported contract ${JSON.stringify(edit)}`,()=>assert.throws(()=>ruleFingerprint({...defaultRule,...edit})));
