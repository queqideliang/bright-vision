import { ComponentIndex } from './component-index.ts';
import type { ComponentFilter, IndexRecord } from './component-index.ts';

let index: ComponentIndex | null = null;
self.onmessage = (event: MessageEvent<{ type: 'build'; records: IndexRecord[] } | { type: 'query'; request: number; filter: ComponentFilter }>) => {
  try {
    if (event.data.type === 'build') {
      index = new ComponentIndex(event.data.records);
      self.postMessage({ type: 'ready' });
    } else {
      if (!index) throw new Error('构件索引尚未就绪。');
      const result = index.query(event.data.filter);
      self.postMessage({ type: 'result', request: event.data.request, result }, { transfer: [result.indices.buffer, result.rows.buffer] });
    }
  } catch (error) {
    self.postMessage({ type: 'error', message: error instanceof Error ? error.message : '构件索引处理失败。' });
  }
};
