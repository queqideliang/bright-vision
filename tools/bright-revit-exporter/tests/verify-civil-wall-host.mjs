// Copyright 2026 崛亮视觉 (Bright Vision). SPDX-License-Identifier: Apache-2.0
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile, realpath, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseCsv } from './verify-civil-host.mjs';

const MODULE = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const WORKSPACE = path.resolve(MODULE, '../..');
const PHASE = path.join(WORKSPACE, 'test/revit-civil-acceptance/phase3-20261028');
const RULE = 'BV.Wall.RectSolid/1';
const EXPECTED = { 'no-opening': { openings: 0, area: 0, result: 6 }, 'one-opening': { openings: 1, area: 2, result: 5.6 }, 'overlapping-openings': { openings: 2, area: 3, result: 5.4 } };
const MAIN_HEADER = ['墙ElementId','墙UniqueId','名称','类型UniqueId','状态','长度m','高度m','厚度m','基准体积m3','洞口并集面积m2','扣减体积m3','项目试算体积m3','Revit原生体积m3','原生体积状态','原生体积来源','基底标高m','基底来源','实际实体体积m3','双向差集体积m3','几何容差m3','墙间检查完整','重叠候选数','相交墙ElementIds','方法','状态说明'];
const LEDGER_HEADER = ['墙ElementId','墙UniqueId','步骤','关联ElementId','关联UniqueId','说明','新增并集面积m2','体积变化m3','公式','来源','方法'];
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const samePath = (a,b) => process.platform === 'win32' ? path.resolve(a).toLowerCase() === path.resolve(b).toLowerCase() : path.resolve(a) === path.resolve(b);
function near(actual, expected, label, tolerance = Math.max(1e-8, Math.abs(expected) * 1e-8)) {
  assert.ok(typeof actual === 'number' && Number.isFinite(actual) && Number.isFinite(expected) && Math.abs(actual - expected) <= tolerance, `${label}: ${actual} != ${expected}`);
}
function unique(values, label) { assert.ok(Array.isArray(values) && values.every(v => typeof v === 'string' && v.length > 0) && values.length === new Set(values).size, label); }
function text(value) { let s = value == null ? '' : String(value); if (/^[\t\r\n]/u.test(s) || /^[=+@-]/u.test(s.trimStart())) s = `'${s}`; return s; }
function number(cell) { assert.match(cell, /^-?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?$/iu, 'Invalid/empty CSV numeric cell'); const v=Number(cell); assert.ok(Number.isFinite(v)); return v; }
function only(rows, label) { assert.ok(Array.isArray(rows) && rows.length === 1, label); return rows[0]; }
function filename(root,name) { assert.ok(name === path.basename(name) && !/[\\/]/u.test(name) && name !== '.' && name !== '..'); return path.join(root,name); }
async function identity(file, max = 128*1024*1024) {
  assert.ok(path.isAbsolute(file)); const s=await stat(file); assert.ok(s.isFile() && s.size <= max, 'Evidence is not a bounded regular file');
  const bytes=await readFile(file); return {path:await realpath(file),bytes:bytes.length,sha256:hash(bytes)};
}
async function matches(file, expected, size) { const id=await identity(path.resolve(file)); assert.match(expected,/^[a-f0-9]{64}$/iu); assert.equal(id.sha256,expected.toLowerCase(),file); if(size != null) assert.equal(id.bytes,size,file); return id; }
async function json(file) { const id=await identity(path.resolve(file)); const bytes=await readFile(file); return {data:JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(bytes).replace(/^\ufeff/u,'')),identity:id}; }

// Independent elementary-cell partition, not the production sweep/merge code.
export function openingUnion(rects, length, height) {
  const clipped=rects.map(r=>({x0:Math.max(0,r.x0),x1:Math.min(length,r.x1),z0:Math.max(0,r.z0),z1:Math.min(height,r.z1)})).filter(r=>r.x1>r.x0&&r.z1>r.z0);
  const xs=[...new Set([0,length,...clipped.flatMap(r=>[r.x0,r.x1])])].sort((a,b)=>a-b);
  const zs=[...new Set([0,height,...clipped.flatMap(r=>[r.z0,r.z1])])].sort((a,b)=>a-b);
  let area=0;
  for(let x=1;x<xs.length;x++) for(let z=1;z<zs.length;z++) {
    const mx=(xs[x]+xs[x-1])/2,mz=(zs[z]+zs[z-1])/2;
    if(clipped.some(r=>mx>r.x0&&mx<r.x1&&mz>r.z0&&mz<r.z1)) area+=(xs[x]-xs[x-1])*(zs[z]-zs[z-1]);
  }
  return area;
}

export function verifyWallCase(name, native, facts, calculation, csvText, sdk, createdIds) {
  const expected=EXPECTED[name]; assert.ok(expected,'Unknown accepted case');
  const source=only(native.Elements,'One native fixture source required'),fact=only(facts.Walls,'One wall fact required'),result=only(calculation.Results,'One calculation result required');
  assert.equal(fact.UniqueId,source.UniqueId); assert.equal(fact.ElementId,source.ElementId); assert.equal(sdk.wallUniqueId,fact.UniqueId);
  assert.equal(fact.UniqueId,createdIds[0]); assert.equal(facts.GeometryViewUniqueId,createdIds[2]); assert.ok(facts.GeometryViewName && /^\d+$/u.test(facts.GeometryPhaseId));
  assert.deepEqual(facts.NativeSnapshot,native,'Reader mutated native snapshot'); assert.deepEqual(calculation.Input,facts,'Calculator input differs from reader facts'); assert.deepEqual(result.Fact,fact);
  assert.equal(source.KindCode,'wall'); assert.equal(source.Supported,true); assert.equal(fact.Status,'verified'); assert.equal(fact.GeometryVerified,true); assert.equal(result.Status,'calculated');
  assert.equal(fact.InterwallOverlapCheckComplete,true);assert.deepEqual(fact.IntersectingWallElementIds,[]);assert.ok(Number.isInteger(fact.OverlapCandidateCount)&&fact.OverlapCandidateCount>=0&&fact.OverlapCandidateCount<=64);
  for(const [key,value,sourceName] of [['ParameterLength',10,'CURVE_ELEM_LENGTH'],['ParameterHeight',3,'WALL_USER_HEIGHT_PARAM'],['ParameterWidth',0.2,'WallType.Width']]) {
    const metric=fact[key]; assert.equal(metric.Status,'available');assert.equal(metric.Unit,'m');assert.equal(metric.Source,sourceName);near(metric.Value,value,key);
  }
  for(const key of ['Length','Area','Volume']) assert.deepEqual(fact[`Native${key}`],source[key],`Native ${key} changed`);
  near(fact.NativeVolume.Value,sdk.nativeRawVolumeFeet3*(0.3048**3),'Independent raw native volume conversion');assert.equal(fact.NativeVolume.Source,'HOST_VOLUME_COMPUTED');assert.equal(fact.NativeVolume.Unit,'m3');assert.equal(fact.NativeVolume.Status,'available');
  assert.ok(Number.isFinite(fact.GeometryToleranceMetres3)&&fact.GeometryToleranceMetres3>0&&fact.GeometryToleranceMetres3<=Math.max(1e-6,6e-7));
  near(fact.GeometryVolumeMetres3,expected.result,'Independent fixture geometry volume',fact.GeometryToleranceMetres3);
  assert.ok(Number.isFinite(fact.SymmetricDifferenceMetres3)&&fact.SymmetricDifferenceMetres3>=0&&fact.SymmetricDifferenceMetres3<=fact.GeometryToleranceMetres3);
  assert.equal(fact.AttachedTop,false);assert.equal(fact.AttachedBottom,false);assert.equal(fact.TopConstraintId,'-1');assert.equal(fact.JoinedElementIds.length,0);assert.equal(fact.EndpointJoinedElementIds.length,0);
  assert.equal(fact.BaseElevationSource,'Level.ProjectElevation + WALL_BASE_OFFSET');assert.ok(Number.isFinite(fact.BaseElevationMetres));
  assert.equal(fact.Inserts.length,expected.openings);unique(fact.Inserts.map(i=>i.UniqueId),'Opening identities missing/duplicated');
  assert.deepEqual([...fact.Inserts.map(i=>i.UniqueId)].sort(),createdIds.slice(3,3+expected.openings).sort(),'Opening identities differ from the created native fixture');
  const inserts=[...fact.Inserts].sort((a,b)=>a.UniqueId<b.UniqueId?-1:a.UniqueId>b.UniqueId?1:0);
  const rectangles=[];
  for(const i of inserts) {
    assert.equal(i.HostUniqueId,fact.UniqueId);assert.equal(i.Kind,'rect-opening');assert.equal(i.Status,'recognized');assert.ok(i.Source.includes('Opening.BoundaryRect'));
    for(const key of ['RectX0','RectX1','RectZ0','RectZ1']) assert.ok(Number.isFinite(i[key]));
    near(i.RectX1-i.RectX0,2,'Known 2m opening width');near(i.RectZ0,1,'Known opening sill');near(i.RectZ1,2,'Known opening head');
    rectangles.push({x0:i.RectX0,x1:i.RectX1,z0:i.RectZ0,z1:i.RectZ1});
  }
  const union=openingUnion(rectangles,10,3);near(union,expected.area,'Independent opening union');
  near(result.BasisVolumeMetres3,6,'Basis must be L*H*T, not already-cut native volume');near(result.OpeningUnionAreaMetres2,union,'Opening union');near(result.DeductionVolumeMetres3,union*0.2,'Opening deduction');near(result.ResultVolumeMetres3,6-union*0.2,'Independent final quantity');
  const ledger=result.Ledger;assert.equal(ledger.length,1+inserts.length);assert.equal(ledger[0].Kind,'basis');assert.equal(ledger[0].UniqueId,fact.UniqueId);assert.equal(ledger[0].ElementId,fact.ElementId);near(ledger[0].AreaMetres2,30,'Basis face area');near(ledger[0].DeltaVolumeMetres3,6,'Basis ledger quantity');
  let previous=0;
  for(let index=0;index<inserts.length;index++) {
    const entry=ledger[index+1],insert=inserts[index],next=openingUnion(rectangles.slice(0,index+1),10,3);
    assert.equal(entry.Kind,'deduction');assert.equal(entry.UniqueId,insert.UniqueId);assert.equal(entry.ElementId,insert.ElementId);assert.equal(entry.Source,insert.Source);near(entry.AreaMetres2,next-previous,'Per-source incremental union');near(entry.DeltaVolumeMetres3,-(next-previous)*0.2,'Per-source signed deduction');previous=next;
  }
  near(ledger.reduce((sum,e)=>sum+e.DeltaVolumeMetres3,0),result.ResultVolumeMetres3,'Ledger conservation');
  assert.equal(sdk.openingCount,expected.openings);assert.equal(sdk.ledgerCount,ledger.length);near(sdk.expectedBasisMetres3,6,'Host fixture basis');near(sdk.expectedOpeningUnionAreaMetres2,expected.area,'Host opening area');near(sdk.expectedResultMetres3,expected.result,'Host fixture result');near(sdk.geometryVolumeMetres3,fact.GeometryVolumeMetres3,'Host geometry recording');near(sdk.symmetricDifferenceMetres3,fact.SymmetricDifferenceMetres3,'Host proof recording');near(sdk.geometryToleranceMetres3,fact.GeometryToleranceMetres3,'Host tolerance recording');near(sdk.nativeVolumeMetres3,fact.NativeVolume.Value,'Host native recording');assert.equal(sdk.nativeSnapshotUnchanged,true);assert.equal(sdk.nativeIsNotSubtractionBasis,true);
  verifyCsv(csvText,facts,result);
  return {name,wallUniqueId:fact.UniqueId,openings:inserts.length,openingUnionAreaMetres2:union,basisMetres3:6,resultMetres3:6-union*0.2,ledgerEntries:ledger.length,geometryToleranceMetres3:fact.GeometryToleranceMetres3,csvVerified:true};
}

function verifyCsv(csvText,facts,result) {
  const blank=r=>r.length===0||(r.length===1&&r[0]==='');
  const rows=parseCsv(csvText.replace(/^\ufeff/u,'')),mainIndex=rows.findIndex(r=>r.length===25&&r[0]===MAIN_HEADER[0]),ledgerIndex=rows.findIndex(r=>r.length===11&&r[2]==='步骤');
  assert.ok(mainIndex>0&&ledgerIndex>mainIndex);assert.deepEqual(rows[mainIndex],MAIN_HEADER);assert.deepEqual(rows[ledgerIndex],LEDGER_HEADER);
  const metadata=new Map(rows.slice(0,mainIndex).filter(r=>r.length===2));assert.equal(metadata.get('产品'),'崛亮视觉 · Bright Vision');assert.equal(metadata.get('方法'),RULE);assert.equal(metadata.get('来源文档标识'),facts.NativeSnapshot.DocumentScope);assert.equal(metadata.get('几何校验视图UniqueId'),facts.GeometryViewUniqueId);assert.equal(metadata.get('几何校验阶段Id'),facts.GeometryPhaseId);
  const data=[];for(let i=mainIndex+1;i<rows.length&&!blank(rows[i]);i++)data.push(rows[i]);const row=only(data,'CSV must include the exact selected fixture wall');assert.equal(row.length,25);
  const f=result.Fact;assert.equal(row[0],text(f.ElementId));assert.equal(row[1],text(f.UniqueId));assert.equal(row[2],text(f.Name));assert.equal(row[3],text(f.TypeUniqueId));assert.equal(row[4],'calculated');
  const numerical=[f.ParameterLength.Value,f.ParameterHeight.Value,f.ParameterWidth.Value,result.BasisVolumeMetres3,result.OpeningUnionAreaMetres2,result.DeductionVolumeMetres3,result.ResultVolumeMetres3,f.NativeVolume.Value];
  numerical.forEach((expected,i)=>near(number(row[5+i]),expected,`CSV value ${i}`));assert.equal(row[13],f.NativeVolume.Status);assert.equal(row[14],f.NativeVolume.Source);near(number(row[15]),f.BaseElevationMetres,'CSV base elevation');assert.equal(row[16],text(f.BaseElevationSource));
  [f.GeometryVolumeMetres3,f.SymmetricDifferenceMetres3,f.GeometryToleranceMetres3].forEach((v,i)=>near(number(row[17+i]),v,'CSV geometry proof'));assert.equal(row[20],'yes');assert.equal(number(row[21]),f.OverlapCandidateCount);assert.equal(row[22],text(f.IntersectingWallElementIds.join('\n')));assert.equal(row[23],RULE);assert.equal(row[24],text(result.Message));
  const entries=rows.slice(ledgerIndex+1).filter(r=>!blank(r));assert.equal(entries.length,result.Ledger.length);
  for(let i=0;i<entries.length;i++) {
    const r=entries[i],e=result.Ledger[i];assert.equal(r.length,11);assert.deepEqual(r.slice(0,6),[f.ElementId,f.UniqueId,e.Kind,e.ElementId,e.UniqueId,e.Description].map(text));near(number(r[6]),e.AreaMetres2,'CSV ledger area');near(number(r[7]),e.DeltaVolumeMetres3,'CSV ledger signed volume');assert.equal(r[8],text(e.Formula));assert.equal(r[9],text(e.Source));assert.equal(r[10],RULE);
  }
}

export async function verifyCivilWallHost(runDirectory, options={}) {
  const run=path.resolve(runDirectory),output=path.join(run,'output');
  assert.ok(run.startsWith(PHASE+path.sep)&&/^run-[a-f0-9]{32}$/iu.test(path.basename(run)),'Expected an owned phase3 nonce run directory');
  const outputReport=path.join(output,'civil-wall-data-validation.json');
  try {
    const configInput=await json(path.join(run,'config.json')),launchInput=await json(path.join(run,'launch.json')),hostInput=await json(path.join(output,'civil-wall-host-report.json'));
    const config=configInput.data,launch=launchInput.data,report=hostInput.data;
    assert.equal(report.result,'PASS');assert.equal(report.runId,config.runId);assert.equal(launch.runId,config.runId);assert.equal(path.basename(run),`run-${config.runId}`);assert.equal(report.processId,launch.processId);assert.equal(report.revitYear,'2020');assert.ok(samePath(config.outputDirectory,output));assert.equal(launch.configSha256,configInput.identity.sha256);
    assert.equal(launch.transactionsAllowed,true);assert.equal(launch.fixtureRollbackRequired,true);assert.equal(launch.activeViewWritesAllowed,false);assert.equal(report.fixtureRollbackRequired,true);assert.equal(report.sourceSaved,false);assert.equal(report.activeViewWritten,false);assert.equal(report.activeViewUnchanged,true);assert.equal(report.fixtureRolledBack,true);assert.equal(report.fixtureRollbackVerified,true);assert.equal(report.selectionRestored,true);assert.equal(report.liveRevitWallSdkVerified,true);
    assert.equal(report.source.elementCountBefore,report.elementCountAfter);assert.equal(report.source.isModifiedBefore,report.isModifiedAfter);assert.equal(report.source.sha256Before,config.expectedModelSha256);assert.equal(report.sourceSha256After,config.expectedModelSha256);assert.equal(report.sourceHashUnchanged,true);assert.ok(samePath(report.source.path,config.modelPath)&&samePath(config.modelPath,launch.modelPath));assert.equal(launch.modelSha256,config.expectedModelSha256);
    const source=await matches(config.modelPath,config.expectedModelSha256,report.source.bytes),preparedInput=await json(path.join(run,'prepared-model.json')),prepared=preparedInput.data;assert.equal(prepared.sha256,source.sha256);assert.ok(samePath(prepared.copy,config.modelPath));await matches(prepared.original,prepared.sha256,prepared.bytes);
    const buildPath=path.resolve(options.buildManifest??launch.productionBuildManifest),buildInput=await json(buildPath),build=buildInput.data;assert.ok(samePath(buildPath,launch.productionBuildManifest));assert.equal(buildInput.identity.sha256,launch.productionBuildManifestSha256);assert.equal(build.packageId,'BrightVision.RevitExporter');assert.equal(build.packageVersion,'1.7.0');assert.equal(build.sourceFiles.length,24);assert.equal(build.variants.length,14);
    unique(build.sourceFiles.map(f=>f.name),'Source files missing or duplicated');const sources=[];for(const f of build.sourceFiles)sources.push(await matches(filename(MODULE,f.name),f.sha256));
    const expectedIds=[];for(let year=2015;year<=2026;year++)for(const runtime of year>=2025?['net8','net10']:[year>=2021?'net48':'net45'])expectedIds.push(`${year}-${runtime}`);assert.deepEqual(build.variants.map(v=>v.id).sort(),expectedIds.sort());
    const payloads=[];for(const v of build.variants){assert.equal(v.buildStatus,'built');assert.equal(v.relativePath,`payload/${v.id}/Bright.Vision.Revit.dll`);assert.equal(v.files.length,1);payloads.push(await matches(path.join(path.dirname(buildPath),...v.relativePath.split('/')),v.files[0].sha256,v.files[0].size));}
    const target=build.variants.find(v=>v.id==='2020-net45');assert.equal(target.files[0].sha256,config.expectedExporterSha256);assert.equal(report.plugin.version,'1.7.0.0');assert.equal(report.plugin.sha256,config.expectedExporterSha256);assert.equal(launch.exporterSha256,config.expectedExporterSha256);assert.ok(samePath(report.plugin.path,config.exporterPath)&&samePath(launch.exporterPath,config.exporterPath));const plugin=await matches(config.exporterPath,config.expectedExporterSha256,target.files[0].size);
    const installedInput=await json(launch.installedStatePath),installed=installedInput.data;assert.equal(installedInput.identity.sha256,launch.installedStateSha256);assert.equal(installed.packageVersion,'1.7.0');assert.equal(installed.variantId,'2020-net45');assert.equal(installed.packageId,build.packageId);assert.equal(installed.files.find(f=>f.name==='Bright.Vision.Revit.dll')?.sha256,config.expectedExporterSha256);for(const f of installed.files)await matches(filename(installed.targetDirectory,f.name),f.sha256);
    const driverInput=await json(launch.driverBuildManifest),driver=driverInput.data;assert.equal(driverInput.identity.sha256,launch.driverBuildManifestSha256);assert.equal(driver.testOnly,true);assert.equal(driver.expectedProductionVersion,'1.7.0.0');assert.equal(driver.fullClassName,'BrightVision.CivilWallHostAcceptance.App');assert.equal(driver.assemblySha256,config.expectedDriverSha256);assert.equal(report.driver.sha256,config.expectedDriverSha256);assert.equal(launch.driverSha256,config.expectedDriverSha256);assert.ok(samePath(driver.assemblyPath,launch.driver)&&samePath(driver.assemblyPath,report.driver.path));await matches(driver.assemblyPath,config.expectedDriverSha256);await matches(driver.sourcePath,driver.sourceSha256);for(const api of driver.api)await matches(api.path,api.sha256);for(const api of target.sourceApi)await matches(api.path,api.sha256);
    unique(report.fixtureCreatedUniqueIds,'Fixture created IDs duplicated');assert.equal(report.controlledUnknownVoidDomainRefusal,true);assert.equal(report.controlledInvalidProofDomainRefusal,true);assert.equal(report.runtimeFamilyVoidReaderVerified,false);assert.equal(report.nonrectangularNativeWallRefusalVerified,true);assert.equal(report.unjoinedPhysicalOverlapWallRefusalVerified,true);
    const names=report.overlappingNativeOpeningCreated?['no-opening','one-opening','overlapping-openings']:['no-opening','one-opening'];assert.deepEqual(report.cases.map(c=>c.name).sort(),[...names].sort());if(!report.overlappingNativeOpeningCreated)assert.ok(report.overlapCreationLimitation);
    const required=[...names.flatMap(n=>[`${n}-native.json`,`${n}-facts.json`,`${n}-calculation.json`,`${n}.csv`]),'nonrect-profile-facts.json','nonrect-profile-calculation.json','unjoined-overlap-facts.json','unjoined-overlap-calculation.json'];assert.deepEqual(report.artifacts.map(f=>f.name).sort(),[...required].sort());
    const artifacts={};for(const f of report.artifacts)artifacts[f.name]=await matches(filename(output,f.name),f.sha256,f.bytes);
    const cases=[];for(const name of names){const native=(await json(path.join(output,`${name}-native.json`))).data,facts=(await json(path.join(output,`${name}-facts.json`))).data,calculation=(await json(path.join(output,`${name}-calculation.json`))).data;assert.ok(samePath(native.SourcePath,config.modelPath));cases.push(verifyWallCase(name,native,facts,calculation,await readFile(path.join(output,`${name}.csv`),'utf8'),report.cases.find(c=>c.name===name),report.fixtureCreatedUniqueIds));}
    const negative=(await json(path.join(output,'nonrect-profile-facts.json'))).data,negativeBatch=(await json(path.join(output,'nonrect-profile-calculation.json'))).data;assert.deepEqual(negativeBatch.Input,negative);const negativeFact=only(negative.Walls,'One nonrect fixture source'),negativeResult=only(negativeBatch.Results,'One nonrect result');assert.equal(negativeFact.UniqueId,report.nonrectangularWallUniqueId);assert.ok(report.fixtureCreatedUniqueIds.includes(negativeFact.UniqueId));assert.equal(negativeFact.GeometryVerified,false);assert.notEqual(negativeResult.Status,'calculated');assert.equal(negativeResult.ResultVolumeMetres3,null);assert.equal(negativeResult.Ledger.length,0);assert.ok(negativeFact.Issues.length>0);
    const overlap=(await json(path.join(output,'unjoined-overlap-facts.json'))).data,overlapBatch=(await json(path.join(output,'unjoined-overlap-calculation.json'))).data;assert.deepEqual(overlapBatch.Input,overlap);const overlapFact=only(overlap.Walls,'One physically overlapping source'),overlapResult=only(overlapBatch.Results,'One rejected overlap result');assert.equal(overlapFact.UniqueId,report.fixtureCreatedUniqueIds[0]);assert.equal(overlapFact.InterwallOverlapCheckComplete,true);assert.equal(overlapFact.GeometryVerified,false);assert.ok(Number.isInteger(overlapFact.OverlapCandidateCount)&&overlapFact.OverlapCandidateCount>=1&&overlapFact.OverlapCandidateCount<=64);assert.ok(overlapFact.IntersectingWallElementIds.includes(report.overlapWallElementId));assert.equal(report.overlapWallUniqueId,report.fixtureCreatedUniqueIds.at(-1));assert.equal(overlapResult.Status,'unsupported');assert.equal(overlapResult.ResultVolumeMetres3,null);assert.equal(overlapResult.Ledger.length,0);assert.ok(overlapFact.SymmetricDifferenceMetres3<=overlapFact.GeometryToleranceMetres3,'Own shape proof must pass before the independent-wall rejection');const expectedIntersection=(report.overlappingNativeOpeningCreated?5.4:5.6)/2;near(report.interwallOverlapIndependentVolumeMetres3,expectedIntersection,'Known independent half-thickness overlap');near(report.interwallOverlapIndependentRawFeet3*(0.3048**3),expectedIntersection,'Independent raw intersection conversion');
    const result={result:'PASS',runId:config.runId,validatedAtUtc:new Date().toISOString(),verifier:await identity(fileURLToPath(import.meta.url)),parser:await identity(fileURLToPath(new URL('./verify-civil-host.mjs',import.meta.url))),inputs:{config:configInput.identity,launch:launchInput.identity,hostReport:hostInput.identity,build:buildInput.identity,driver:driverInput.identity,installed:installedInput.identity},source,plugin,sourceFiles:sources.length,payloads:payloads.length,artifacts,cases,nonrectangularNativeWallRefused:true,fixtureRollbackVerified:true,sourceSaved:false,sourceHashUnchanged:true,ribbonClickVerified:false,nativeWallWindowVerified:false,limitations:['Node recomputes source rectangle unions, quantities, ledger and CSV; it checks SDK-captured CSG evidence, not a second native geometry session.','Created-element removal, unchanged view/selection and the controlled negative calls are SDK-driver assertions bound to this source/driver hash.','Unknown real family voids, regional rules, other Revit years and native wall UI are not certified by this fixture.','Elapsed SDK sequence is not a product performance benchmark; an unchanged IsModified flag does not certify every in-memory property.']};
    if(options.writeResult!==false)await writeFile(outputReport,JSON.stringify(result,null,2),'utf8');return result;
  } catch(error){if(options.writeResult!==false)await writeFile(outputReport,JSON.stringify({result:'FAIL',runDirectory:run,error:String(error?.stack??error)},null,2),'utf8');throw error;}
}

if(process.argv[1]&&samePath(process.argv[1],fileURLToPath(import.meta.url))){const args=process.argv.slice(2),run=args.shift();let buildManifest;if(args.length){assert.equal(args.shift(),'--build-manifest');buildManifest=args.shift();assert.ok(buildManifest&&args.length===0);}if(!run){console.error('Usage: node tests/verify-civil-wall-host.mjs <phase3-run-directory> [--build-manifest <frozen-manifest>]');process.exitCode=2;}else try{const r=await verifyCivilWallHost(run,{buildManifest});console.log(JSON.stringify({result:r.result,runId:r.runId,cases:r.cases,fixtureRollbackVerified:r.fixtureRollbackVerified,sourceHashUnchanged:r.sourceHashUnchanged}));}catch(error){console.error(String(error?.stack??error));process.exitCode=1;}}
