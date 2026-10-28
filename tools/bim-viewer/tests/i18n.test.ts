import assert from 'node:assert/strict';
import test from 'node:test';
import { englishMessages, getLanguage, locale, setLanguage, tr, translatedMessage, translatedProgress } from '../src/i18n.ts';

test('interface translations switch both ways while preserving source model and viewpoint names', () => {
  const original = getLanguage();
  try {
    setLanguage('zh');
    assert.equal(locale(), 'zh-CN');
    const message = tr('已恢复视点：{0}', { '0': '三层设备 A' });
    assert.equal(message, '已恢复视点：三层设备 A');
    setLanguage('en');
    assert.equal(locale(), 'en-US');
    assert.equal(tr('打开模型'), 'Open model');
    assert.equal(translatedMessage(message), 'Viewpoint restored: 三层设备 A');
    assert.equal(tr('三层设备 A'), '三层设备 A');
    const english = tr('已选 {0} 个构件', { '0': '1,234' });
    setLanguage('zh');
    assert.equal(translatedMessage(english), '已选 1,234 个构件');
  } finally { setLanguage(original); }
});

test('English loading phases and failures have useful fallbacks and retain original failure diagnostics', () => {
  const original = getLanguage();
  try {
    setLanguage('en');
    assert.equal(translatedProgress('读取楼层和纹理'), 'Reading levels and textures');
    assert.equal(translatedProgress('未来新增的处理阶段'), 'Processing model…');
    assert.equal(translatedMessage('无法解码源文件：构件 12', true), 'The model could not be loaded. Original diagnostic:\n无法解码源文件：构件 12');
    assert.equal(translatedMessage('Network disconnected', true), 'Network disconnected');
    setLanguage('zh');
    assert.equal(translatedMessage('无法解码源文件：构件 12', true), '无法解码源文件：构件 12');
  } finally { setLanguage(original); }
});

test('every English message preserves all interpolation slots from its Chinese source', () => {
  const slots = (message: string) => [...message.matchAll(/\{([^{}]+)\}/g)].map(match => match[1]).sort();
  assert.ok(Object.keys(englishMessages).length > 150);
  for (const [source, english] of Object.entries(englishMessages)) {
    assert.ok(english.trim(), source);
    assert.deepEqual(slots(english), slots(source), source);
    assert.doesNotMatch(english, /[\u3400-\u9fff]/, source);
  }
});
