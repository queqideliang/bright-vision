export interface IndexRecord {
  id: string; name: string; category: string; family: string; type: string;
  categoryCode: string; levels: string[]; triangles: number;
}
export interface ComponentFilter { search: string; level: string; category: string; }
export interface TreeGroup {
  key: string; label: string; depth: number; count: number;
  first: number; last: number; rowEnd: number;
}
export interface CategorySummary { name: string; count: number; triangles: number; }
export interface IndexResult {
  indices: Uint32Array;
  rows: Int32Array;
  groups: TreeGroup[];
  categories: CategorySummary[];
  heavy: { index: number; triangles: number }[];
}
export const UNCLASSIFIED_CATEGORY = '\u0001unclassified';

interface Branch { key: string; label: string; depth: number; count: number; children: Map<string, Branch>; leaves: number[]; }
const normalize = (value: string) => value.toLocaleLowerCase();

/** The search/aggregation index lives in a Worker; only row references return. */
export class ComponentIndex {
  private readonly records: IndexRecord[];
  private readonly searchTexts: string[];
  private readonly byCategory = new Map<string, number[]>();
  private readonly byLevel = new Map<string, number[]>();

  constructor(records: IndexRecord[]) {
    this.records = records;
    this.searchTexts = records.map(record => normalize(`${record.id} ${record.name} ${record.category} ${record.categoryCode} ${record.family} ${record.type}`));
    for (let index = 0; index < records.length; index++) {
      const record = records[index]!;
      this.append(this.byCategory, record.category, index);
      for (const level of new Set(record.levels)) this.append(this.byLevel, level, index);
    }
  }

  private append(map: Map<string, number[]>, key: string, index: number) {
    let values = map.get(key);
    if (!values) { values = []; map.set(key, values); }
    values.push(index);
  }

  query(filter: ComponentFilter): IndexResult {
    const hasCategory = filter.category !== '';
    const categoryKey = filter.category === UNCLASSIFIED_CATEGORY ? '' : filter.category;
    const category = hasCategory ? this.byCategory.get(categoryKey) ?? [] : undefined;
    const level = filter.level ? this.byLevel.get(filter.level) ?? [] : undefined;
    const source = category && level ? (category.length < level.length ? category : level) : category ?? level;
    const search = normalize(filter.search.trim());
    const roots = new Map<string, Branch>();
    const categories = new Map<string, CategorySummary>();
    const heavy: { index: number; triangles: number }[] = [];
    const length = source?.length ?? this.records.length;
    for (let item = 0; item < length; item++) {
      const index = source ? source[item]! : item;
      const record = this.records[index]!;
      if (hasCategory && record.category !== categoryKey) continue;
      if (filter.level && !record.levels.includes(filter.level)) continue;
      if (search && !this.searchTexts[index]!.includes(search)) continue;
      let summary = categories.get(record.category);
      if (!summary) { summary = { name: record.category, count: 0, triangles: 0 }; categories.set(record.category, summary); }
      summary.count++; summary.triangles += record.triangles;
      if (record.triangles > 0 && (heavy.length < 20 || record.triangles > heavy[heavy.length - 1]!.triangles)) {
        heavy.push({ index, triangles: record.triangles }); heavy.sort((a, b) => b.triangles - a.triangles); heavy.length = Math.min(20, heavy.length);
      }
      const labels = [record.category || '未分类', record.family || '未提供族', record.type || '未提供类型'];
      let siblings = roots;
      let node: Branch | undefined;
      for (let depth = 0; depth < labels.length; depth++) {
        const label = labels[depth]!;
        const key = JSON.stringify([record.category, record.family, record.type].slice(0, depth + 1));
        node = siblings.get(key);
        if (!node) { node = { key, label, depth, count: 0, children: new Map(), leaves: [] }; siblings.set(key, node); }
        node.count++; siblings = node.children;
      }
      node!.leaves.push(index);
    }
    const ordered: number[] = [];
    const rows: number[] = [];
    const groups: TreeGroup[] = [];
    const walk = (branches: Map<string, Branch>) => {
      for (const branch of [...branches.values()].sort((a, b) => a.label.localeCompare(b.label, 'zh-CN'))) {
        const group: TreeGroup = { key: branch.key, label: branch.label, depth: branch.depth, count: branch.count, first: ordered.length, last: 0, rowEnd: 0 };
        rows.push(-1 - groups.length); groups.push(group);
        if (branch.children.size) walk(branch.children);
        for (const index of branch.leaves) { ordered.push(index); rows.push(index); }
        group.last = ordered.length; group.rowEnd = rows.length;
      }
    };
    walk(roots);
    return { indices: Uint32Array.from(ordered), rows: Int32Array.from(rows), groups,
      categories: [...categories.values()].sort((a, b) => b.count - a.count), heavy };
  }
}

/** Skip collapsed subtree intervals, without constructing a DOM node per row. */
export function visibleTreeRows(result: Pick<IndexResult, 'rows' | 'groups'>, collapsed: ReadonlySet<string>): Int32Array {
  if (!collapsed.size) return result.rows;
  const visible: number[] = [];
  for (let index = 0; index < result.rows.length; index++) {
    const reference = result.rows[index]!;
    visible.push(reference);
    if (reference < 0) {
      const group = result.groups[-reference - 1]!;
      if (collapsed.has(group.key)) index = group.rowEnd - 1;
    }
  }
  return Int32Array.from(visible);
}
