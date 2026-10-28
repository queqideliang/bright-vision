import type { BimModel } from './model.ts';
import type { ViewerViewpoint } from './viewer/model-viewer.ts';
import { tr } from './i18n.ts';

export interface SavedView {
  id: string; name: string; created: string;
  view: Omit<ViewerViewpoint, 'visibleIds'>;
  visibility: { mode: 'visible' | 'hidden'; ids: string[] };
}

/** A local matching key, not a cryptographic identity or version-diff claim. */
export function modelViewKey(model: BimModel): string {
  let hash = 2166136261;
  const update = (value: string) => { for (let i = 0; i < value.length; i++) hash = Math.imul(hash ^ value.charCodeAt(i), 16777619); };
  update(model.name); update(String(model.components.length));
  for (const component of model.components) {
    update(component.id); update('|');
    update(component.matrix.join(','));
    if (component.min) update(component.min.join(','));
    if (component.max) update(component.max.join(','));
  }
  return `bright-vision.views.1.${(hash >>> 0).toString(16)}`;
}

export function compactView(name: string, view: ViewerViewpoint, allIds: readonly string[]): SavedView {
  const visible = new Set(view.visibleIds);
  const hidden = allIds.filter(id => !visible.has(id));
  const { visibleIds, ...rest } = view;
  return { id: `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`, name: name.trim().slice(0, 80) || tr('未命名视点'),
    created: new Date().toISOString(), view: rest,
    visibility: hidden.length < visibleIds.length ? { mode: 'hidden', ids: hidden } : { mode: 'visible', ids: [...visibleIds] } };
}

export function expandView(saved: SavedView, allIds: readonly string[]): ViewerViewpoint {
  const allowed = new Set(allIds);
  const included = new Set(saved.visibility.ids.filter(id => allowed.has(id)));
  return { ...saved.view, visibleIds: saved.visibility.mode === 'hidden' ? allIds.filter(id => !included.has(id)) : [...included],
    selectedIds: saved.view.selectedIds.filter(id => allowed.has(id)),
    ghostedIds: saved.view.ghostedIds?.filter(id => allowed.has(id)) };
}

function point(value: unknown): boolean {
  return Array.isArray(value) && value.length === 3 && value.every(item => typeof item === 'number' && Number.isFinite(item) && Math.abs(item) < 1e16);
}
function ids(value: unknown): value is string[] { return Array.isArray(value) && value.length <= 2_000_000 && value.every(id => typeof id === 'string' && id.length <= 4096); }

export function readSavedViews(json: string | null): SavedView[] {
  if (!json || json.length > 8 * 1024 * 1024) return [];
  try {
    const values: unknown = JSON.parse(json);
    if (!Array.isArray(values)) return [];
    return values.slice(0, 20).filter((item): item is SavedView => {
      if (!item || typeof item !== 'object') return false;
      const v = item as SavedView;
      if (typeof v.id !== 'string' || typeof v.name !== 'string' || v.name.length > 80 || typeof v.created !== 'string' || !v.view || !v.visibility) return false;
      if (v.view.version !== 1 || !point(v.view.position) || !point(v.view.target) || !ids(v.view.selectedIds) || typeof v.view.ghostMode !== 'boolean') return false;
      if (v.view.ghostedIds !== undefined && !ids(v.view.ghostedIds)) return false;
      const box = v.view.sectionBox;
      if (box !== null && (!box || !point(box.min) || !point(box.max) || box.min.some((number, index) => number > box.max[index]!))) return false;
      return ['hidden', 'visible'].includes(v.visibility.mode) && ids(v.visibility.ids);
    });
  } catch { return []; }
}
