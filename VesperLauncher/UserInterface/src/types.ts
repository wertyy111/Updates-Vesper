export interface HostSnapshot {
  phase: string;
  errorMessage?: string | null;
  update: {
    message?: string;
    detailMessage?: string;
    progressPercent?: number | null;
    isIndeterminate?: boolean;
    progressText?: string;
  };
  launcher?: LauncherSnapshot | null;
}

export interface LauncherSnapshot {
  activeSection: string;
  activeSettingsTab: string;
  isBusy: boolean;
  isGameRunning: boolean;
  canAccessFriends: boolean;
  notificationsCount: number;
  theme: Record<string, any>;
  main: Record<string, any>;
  account: Record<string, any>;
  settings: Record<string, any>;
  skin: Record<string, any>;
  background: Record<string, any>;
  mods: Record<string, any>;
  friends: Record<string, any>;
}

export interface HostEnvelope {
  type: 'snapshot' | 'error';
  data?: HostSnapshot;
  message?: string;
}

export interface GlassTuningSettings {
  refraction: number;
  bevelDepth: number;
  bevelWidth: number;
  frost: number;
  resolution: number;
}

export interface PanelRenderProps {
  launcher: LauncherSnapshot;
  accountForm: { mode: string; username: string; password: string };
  setAccountForm: (form: any) => void;
  setAccountDirty: (dirty: boolean) => void;
  submitAccount: () => void;
  friendDraft: string;
  setFriendDraft: (draft: string) => void;
  setFriendDirty: (dirty: boolean) => void;
  javaPathDraft: string;
  setJavaPathDraft: (draft: string) => void;
  setJavaPathDirty: (dirty: boolean) => void;
  jvmArgsDraft: string;
  setJvmArgsDraft: (draft: string) => void;
  setJvmArgsDirty: (dirty: boolean) => void;
  toggleSelectedMod: (projectId: string) => void;
  glassTuning: GlassTuningSettings;
  setGlassTuningValue: (field: keyof GlassTuningSettings, value: number) => void;
}
