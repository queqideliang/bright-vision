// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import test from 'node:test';
import { openingUnion,verifyWallCase,verifyNativeSnapshots,verifyHostCompletion,verifyRejectedOverlapShape } from './verify-civil-wall-host.mjs';
const IDS=['wall-uid','type-uid','view-uid','opening-a','opening-b','profile-uid'];
const SOURCES=['CURVE_ELEM_LENGTH','WALL_USER_HEIGHT_PARAM','WallType.Width'];
const MAIN=['墙ElementId','墙UniqueId','名称','类型UniqueId','状态','长度m','高度m','厚度m','基准体积m3','洞口并集面积m2','扣减体积m3','项目试算体积m3','Revit原生体积m3','原生体积状态','原生体积来源','基底标高m','基底来源','实际实体体积m3','双向差集体积m3','几何容差m3','墙间检查完整','重叠候选数','相交墙ElementIds','方法','状态说明'];
const LEDGER=['墙ElementId','墙UniqueId','步骤','关联ElementId','关联UniqueId','说明','新增并集面积m2','体积变化m3','公式','来源','方法'];
const metric=(value,unit,source)=>({Status:'available',Value:value,Unit:unit,Source:source});
function protectedText(value){let s=String(value??'');if(/^[\t\r\n]/u.test(s)||/^[=+@-]/u.test(s.trimStart()))s=`'${s}`;return s;}
function csvLine(cells){return cells.map(v=>`"${(typeof v==='number'?String(v):protectedText(v)).replaceAll('"','""')}"`).join(',')+'\r\n';}
function fixture(name){
  const count=name==='no-opening'?0:name==='one-opening'?1:2,area=count===0?0:count===1?2:3,volume=6-area*0.2;
  const row={ElementId:'10',UniqueId:IDS[0],KindCode:'wall',Supported:true,Length:metric(10,'m','CURVE_ELEM_LENGTH'),Area:metric(30-area,'m2','HOST_AREA_COMPUTED'),Volume:metric(volume,'m3','HOST_VOLUME_COMPUTED')};
  const native={DocumentScope:'native-scope',SourcePath:'H:\\fixture\\sample.rvt',Elements:[row]};
  const inserts=Array.from({length:count},(_,i)=>({ElementId:String(20+i),UniqueId:IDS[3+i],HostUniqueId:IDS[0],Kind:'rect-opening',Status:'recognized',Source:'Opening.Host + Opening.BoundaryRect',RectX0:2+i,RectX1:4+i,RectZ0:1,RectZ1:2}));
  const fact={ElementId:'10',UniqueId:IDS[0],Name:'=controlled synthetic fixture',TypeUniqueId:IDS[1],Status:'verified',GeometryVerified:true,InterwallOverlapCheckComplete:true,OverlapCandidateCount:0,IntersectingWallElementIds:[],ParameterLength:metric(10,'m',SOURCES[0]),ParameterHeight:metric(3,'m',SOURCES[1]),ParameterWidth:metric(0.2,'m',SOURCES[2]),NativeLength:row.Length,NativeArea:row.Area,NativeVolume:row.Volume,GeometryVolumeMetres3:volume,GeometryToleranceMetres3:6e-8,SymmetricDifferenceMetres3:0,AttachedTop:false,AttachedBottom:false,TopConstraintId:'-1',JoinedElementIds:[],EndpointJoinedElementIds:[],BaseElevationMetres:-2.7,BaseElevationSource:'Level.ProjectElevation + WALL_BASE_OFFSET',Inserts:inserts};
  const ledger=[{Kind:'basis',ElementId:'10',UniqueId:IDS[0],Description:'basis',Formula:'10 x 3 x 0.2',AreaMetres2:30,DeltaVolumeMetres3:6,Source:SOURCES.join('; ')}];
  inserts.forEach((i,index)=>ledger.push({Kind:'deduction',ElementId:i.ElementId,UniqueId:i.UniqueId,Description:'rect opening',Formula:index===0?'(2 - 0) x 0.2':'(3 - 2) x 0.2',Source:i.Source,AreaMetres2:index===0?2:1,DeltaVolumeMetres3:index===0?-0.4:-0.2}));
  const facts={NativeSnapshot:native,GeometryViewUniqueId:IDS[2],GeometryViewName:'explicit geometry',GeometryPhaseId:'1',Walls:[fact]};
  const result={Fact:fact,Status:'calculated',BasisVolumeMetres3:6,OpeningUnionAreaMetres2:area,DeductionVolumeMetres3:area*0.2,ResultVolumeMetres3:volume,Message:'project method, not regional quantity',Ledger:ledger};
  const calculation={Input:facts,Results:[result]};
  const sdk={wallUniqueId:IDS[0],nativeRawVolumeFeet3:volume/(0.3048**3),nativeVolumeMetres3:volume,expectedBasisMetres3:6,expectedOpeningUnionAreaMetres2:area,expectedResultMetres3:volume,geometryVolumeMetres3:volume,symmetricDifferenceMetres3:0,geometryToleranceMetres3:6e-8,nativeSnapshotUnchanged:true,nativeIsNotSubtractionBasis:true,openingCount:count,ledgerCount:ledger.length};
  let csv='\ufeff'+[['产品','崛亮视觉 · Bright Vision'],['方法','BV.Wall.RectSolid/1'],['来源文档标识','native-scope'],['几何校验视图UniqueId',IDS[2]],['几何校验阶段Id','1']].map(csvLine).join('');
  csv+=csvLine([])+csvLine(MAIN)+csvLine(['10',IDS[0],fact.Name,IDS[1],'calculated',10,3,0.2,6,area,area*0.2,volume,volume,'available','HOST_VOLUME_COMPUTED',-2.7,fact.BaseElevationSource,volume,0,6e-8,'yes',0,'','BV.Wall.RectSolid/1',result.Message]);
  csv+=csvLine([])+csvLine(['逐条台账'])+csvLine(LEDGER)+ledger.map(e=>csvLine(['10',IDS[0],e.Kind,e.ElementId,e.UniqueId,e.Description,e.AreaMetres2,e.DeltaVolumeMetres3,e.Formula,e.Source,'BV.Wall.RectSolid/1'])).join('');
  return {name,native,facts,calculation,csv,sdk};
}
const check=f=>verifyWallCase(f.name,f.native,f.facts,f.calculation,f.csv,f.sdk,IDS);
for(const name of ['no-opening','one-opening','overlapping-openings'])test(`synthetic ${name} passes independent quantity/CSV check`,()=>assert.equal(check(fixture(name)).csvVerified,true));
test('independent cell partition clips and deduplicates rectangles',()=>assert.equal(openingUnion([{x0:-2,x1:4,z0:1,z1:2},{x0:2,x1:5,z0:1,z1:2},{x0:2,x1:5,z0:1,z1:2}],10,3),5));
for(const [label,edit] of [
  ['double deduction from native net volume',f=>{f.calculation.Results[0].BasisVolumeMetres3=5.6;}],
  ['native raw reference tamper',f=>{f.sdk.nativeRawVolumeFeet3+=1;}],
  ['CSG proof residual outside tolerance',f=>{f.facts.Walls[0].SymmetricDifferenceMetres3=1;}],
  ['arbitrarily enlarged geometry tolerance',f=>{f.facts.Walls[0].GeometryToleranceMetres3=0.1;}],
  ['incomplete interwall check',f=>{f.facts.Walls[0].InterwallOverlapCheckComplete=false;}],
  ['physical wall overlap accepted',f=>{f.facts.Walls[0].IntersectingWallElementIds=['other-wall'];}],
  ['unknown family insert accepted',f=>{f.facts.Walls[0].Inserts[0].Kind='door';}],
  ['wrong host identity',f=>{f.facts.Walls[0].Inserts[0].HostUniqueId='another-wall';}],
  ['ledger signed contribution changed',f=>{f.calculation.Results[0].Ledger[1].DeltaVolumeMetres3=-0.8;}],
  ['CSV selected member changed',f=>{f.csv=f.csv.replace('"10","wall-uid"','"10","foreign-wall"');}],
  ['CSV number changed',f=>{f.csv=f.csv.replace('"5.6","5.6"','"5.2","5.6"');}],
])test(`synthetic tamper rejected: ${label}`,()=>{const f=fixture('one-opening');edit(f);assert.throws(()=>check(f));});

function snapshotFixture(){const native=fixture('one-opening').native;return {native,before:structuredClone(native),afterReader:structuredClone(native),afterCalculation:structuredClone(native),comparison:{schemaVersion:1,method:'exact-structural-json-v1',result:'PASS',beforeFile:'one-opening-native-before.json',afterReaderFile:'one-opening-native-after-reader.json',afterCalculationFile:'one-opening-native-after-calculation.json',objectKeyOrderIgnored:true,arrayOrderPreserved:true,scalarTypeAndValueExact:true,readerDifferencePath:null,calculationDifferencePath:null,firstDifferencePath:null}};}
const checkSnapshots=f=>verifyNativeSnapshots('one-opening',f.before,f.afterReader,f.afterCalculation,f.native,f.comparison);
test('snapshot object property reordering preserves exact values',()=>{const f=snapshotFixture();f.afterReader=Object.fromEntries(Object.entries(f.afterReader).reverse());assert.equal(checkSnapshots(f).beforeAfterReaderVerified,true);});
for(const [label,edit] of [
  ['numeric value changed',f=>{f.afterReader.Elements[0].Volume.Value+=0.1;}],
  ['field added',f=>{f.afterCalculation.NewField='unexpected';}],
  ['field deleted',f=>{delete f.afterReader.Elements[0].UniqueId;}],
  ['scalar type changed',f=>{f.afterCalculation.Elements[0].Volume.Value='5.6';}],
  ['array reordered',f=>{f.before.Notices=['first','second'];f.native.Notices=['first','second'];f.afterReader.Notices=['second','first'];f.afterCalculation.Notices=['first','second'];}],
  ['recorded difference ignored',f=>{f.comparison.firstDifferencePath='$.Elements[0].Volume.Value';}],
])test(`snapshot tamper rejected: ${label}`,()=>{const f=snapshotFixture();edit(f);assert.throws(()=>checkSnapshots(f));});

function terminalFixture(){return {report:{result:'PASS',runId:'owned-run',finishedUtc:'2026-10-28T13:02:00.000Z',elapsedSeconds:100},status:{phase:'completed',runId:'owned-run',utc:'2026-10-28T13:02:00.100Z'},launch:{runId:'owned-run',startedAtUtc:'2026-10-28T13:00:00.000Z'}};}
const checkTerminal=f=>verifyHostCompletion(f.report,f.status,f.launch,Date.parse('2026-10-28T13:03:00.000Z'));
test('matching completed terminal evidence is accepted',()=>assert.equal(checkTerminal(terminalFixture()).phase,'completed'));
for(const [label,edit] of [
  ['missing status',f=>{delete f.status;}],
  ['failed status beside PASS report',f=>{f.status.phase='failed';}],
  ['wrong nonce status',f=>{f.status.runId='another-run';}],
  ['status older than report',f=>{f.status.utc='2026-10-28T13:01:59.999Z';}],
  ['finish before launch',f=>{f.report.finishedUtc='2026-10-28T12:59:59.999Z';}],
  ['missing finish timestamp',f=>{delete f.report.finishedUtc;}],
])test(`terminal evidence rejected: ${label}`,()=>{const f=terminalFixture();edit(f);assert.throws(()=>checkTerminal(f));});

test('rejected overlap retains a valid independently known own-shape proof',()=>assert.equal(verifyRejectedOverlapShape(fixture('one-opening').facts.Walls[0],5.6),true));
for(const key of ['SymmetricDifferenceMetres3','GeometryToleranceMetres3','GeometryVolumeMetres3'])test(`rejected overlap null ${key} with recomputed hash is rejected`,()=>{const fact=structuredClone(fixture('one-opening').facts.Walls[0]);fact[key]=null;const bytes=Buffer.from(JSON.stringify(fact));assert.match(createHash('sha256').update(bytes).digest('hex'),/^[a-f0-9]{64}$/u);assert.throws(()=>verifyRejectedOverlapShape(JSON.parse(bytes.toString('utf8')),5.6));});
