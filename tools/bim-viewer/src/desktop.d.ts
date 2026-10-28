import type { LoadProgress } from './model.ts';
import type { ArchiveChoice } from './import/packages.ts';
export interface ArchiveReference { url: string; name: string; size: number; cacheId: string }
export interface DesktopUpdateState {
  status: 'not-configured' | 'up-to-date' | 'update-available' | 'staged' | 'error';
  currentVersion: string;
  latestVersion?: string | null;
  source?: string;
  sourceKind?: 'local' | 'github';
  officialConfigured?: boolean;
  sourceUrl?: string;
  repositoryDisplay?: string;
  manifestVersion?: number;
  channel?: 'preview' | 'stable';
  subscriptionChannel?: 'preview' | 'stable';
  releaseChannel?: 'preview' | 'stable' | null;
  busy?: boolean;
  code?: string;
  checkedAt?: string | null;
}
export interface DesktopRevitState {
  status: 'not-needed' | 'ready' | 'installed' | 'deferred' | 'failed' | 'busy';
  exporterVersion: string;
  hosts: Array<{ year: string; runtime: string; managed: boolean; status: 'installed' | 'ready' | 'unsupported'; managedVersion?: string }>;
  message?: string;
  code?: string;
}
declare global {
  interface Window {
    desktopApp?: {
      getInfo(): Promise<{ version: string; language: 'zh' | 'en'; update: DesktopUpdateState }>;
      setLanguage(language: 'zh' | 'en'): Promise<'zh' | 'en'>;
      checkUpdates(): Promise<DesktopUpdateState>;
      chooseUpdateSource(): Promise<DesktopUpdateState>;
      useOfficialUpdates(): Promise<DesktopUpdateState>;
      installUpdate(): Promise<DesktopUpdateState>;
      onUpdate(callback: (state: DesktopUpdateState) => void): () => void;
      getRevitStatus(): Promise<DesktopRevitState>;
      installRevitPlugin(): Promise<DesktopRevitState>;
      uninstallRevitPlugin(): Promise<DesktopRevitState>;
      onRevitStatus(callback: (state: DesktopRevitState) => void): () => void;
    };
    localBim?: {
      openArchiveEntry(file: File | ArchiveReference, choice: ArchiveChoice): Promise<ArchiveReference>;
      cancelArchiveRead(): void;
      onProgress(callback: (progress: LoadProgress) => void): void;
    };
  }
}
