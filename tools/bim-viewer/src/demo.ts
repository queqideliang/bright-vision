import type { BimModel } from './model.ts';

export function createDemoModel(): BimModel {
  const identity = () => new Float64Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
  const components = [
    { id: 'demo-wall', name: '示例墙', category: '墙', family: '基本墙', type: '演示墙', x: 0, z: 0, sx: 6, sy: .3, sz: 3, level: '一层', material: 'wall' },
    { id: 'demo-slab', name: '一层楼板', category: '楼板', family: '楼板', type: '演示楼板', x: 0, z: -.3, sx: 6, sy: 4, sz: .3, level: '一层', material: 'slab' },
    { id: 'demo-column', name: '示例柱', category: '结构柱', family: '矩形柱', type: '演示柱', x: 5.5, z: 0, sx: .5, sy: .5, sz: 3, level: '一层', material: 'column' },
    { id: 'demo-upper', name: '二层楼板', category: '楼板', family: '楼板', type: '演示楼板', x: 0, z: 3, sx: 6, sy: 4, sz: .3, level: '二层', material: 'slab' },
  ];
  return {
    name: '演示模型 · 合成数据',
    components: components.map((c) => {
      const matrix = identity();
      matrix[0] = c.sx; matrix[5] = c.sy; matrix[10] = c.sz; matrix[12] = c.x; matrix[14] = c.z;
      return { id: c.id, name: c.name, category: c.category, family: c.family, type: c.type,
        matrix, nodeIds: [c.id], levelNames: [c.level],
        properties: { '标识数据': { '说明': '此为合成演示数据，不是实际工程模型', '构件编号': c.id }, '尺寸': { '示例长度': c.sx, '示例高度': c.sz } },
        typeProperties: { '类型信息': { '类型名称': c.type } }, familyProperties: {} };
    }),
    nodes: Object.fromEntries(components.map((c) => [c.id, { id: c.id, geometryId: 'cube', materialId: c.material, matrix: identity() }])),
    geometries: { cube: { id: 'cube',
      positions: new Float32Array([0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1]),
      indices: new Uint32Array([0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7]) } },
    materials: { wall: { id: 'wall', raw: { color: '#c9d8e7', opacity: 1 } }, slab: { id: 'slab', raw: { color: '#b1bccb', opacity: 1 } }, column: { id: 'column', raw: { color: '#427cb6', opacity: 1 } } },
    textures: {}, levels: [{ name: '一层', elevation: 0, componentIds: ['demo-wall', 'demo-slab', 'demo-column'] }, { name: '二层', elevation: 3, componentIds: ['demo-upper'] }],
    warnings: [], stats: { components: 4, geometries: 1, triangles: 12, nodes: 4 },
  };
}
