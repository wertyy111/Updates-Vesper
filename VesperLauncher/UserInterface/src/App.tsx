import { startTransition, useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type MouseEvent } from 'react';
import { photinoBridge } from './bridge';
import type { PointerEvent as ReactPointerEvent } from 'react';
import type { GlassTuningSettings, HostSnapshot, PanelRenderProps } from './types';

import { AccountPanel } from './components/panels/AccountPanel';
import { SettingsPanel } from './components/panels/SettingsPanel';
import { SkinPanel } from './components/panels/SkinPanel';
import { BackgroundPanel } from './components/panels/BackgroundPanel';
import { ModsPanel } from './components/panels/ModsPanel';
import { FriendsPanel } from './components/panels/FriendsPanel';
import { EmptyPanel } from './components/panels/EmptyPanel';
import { LoadingScreen } from './components/common/LoadingScreen';
import html2canvasScriptUrl from './vendor/html2canvas.min.js?url';
import liquidGlScriptUrl from './vendor/liquidGL.js?url';

const BOTTOM_SECTION_ITEMS = [
  { id: 'skin', label: 'Скин' },
  { id: 'background', label: 'Фон' },
  { id: 'mods', label: 'Моды' },
  { id: 'friends', label: 'Друзья' }
];

const WPF_WIDE_PANEL_SECTIONS = new Set(['account', 'settings', ...BOTTOM_SECTION_ITEMS.map((section) => section.id)]);
const SETTINGS_WIDTH_PANEL_SECTIONS = new Set(['account', 'settings', ...BOTTOM_SECTION_ITEMS.map((section) => section.id)]);

const DEFAULT_GLASS_TUNING: GlassTuningSettings = {
  refraction: 0.014,
  bevelDepth: 0.02,
  bevelWidth: 0.15,
  frost: 0,
  resolution: 3
};

function App() {
  const [hostSnapshot, setHostSnapshot] = useState<HostSnapshot | null>(null);
  const [runtimeError, setRuntimeError] = useState<string | null>(null);
  const [glassTuning, setGlassTuning] = useState<GlassTuningSettings>(DEFAULT_GLASS_TUNING);
  const [accountForm, setAccountForm] = useState({ mode: 'login', username: '', password: '' });
  const [accountDirty, setAccountDirty] = useState(false);
  const [friendDraft, setFriendDraft] = useState('');
  const [friendDirty, setFriendDirty] = useState(false);
  const [javaPathDraft, setJavaPathDraft] = useState('');
  const [javaPathDirty, setJavaPathDirty] = useState(false);
  const [jvmArgsDraft, setJvmArgsDraft] = useState('');
  const [jvmArgsDirty, setJvmArgsDirty] = useState(false);
  const [versionMenuOpen, setVersionMenuOpen] = useState(false);
  const [versionMenuStyle, setVersionMenuStyle] = useState<CSSProperties>({});
  const versionPickerButtonRef = useRef<HTMLButtonElement | null>(null);
  const settingsLiquidButtonRef = useRef<HTMLButtonElement | null>(null);
  const liquidGlassBatchRef = useRef(0);
  const liquidGlassResolutionRef = useRef(glassTuning.resolution);
  const clientReadySentRef = useRef(false);

  const launcher = hostSnapshot?.launcher ?? null;
  const theme = launcher?.theme ?? {};
  const main = launcher?.main ?? {};
  const account = launcher?.account ?? {};
  const settings = launcher?.settings ?? {};
  const mods = launcher?.mods ?? {};
  const friends = launcher?.friends ?? {};
  const loadingState = !hostSnapshot || !launcher;
  const activeSection = launcher?.activeSection ?? 'none';

  const setGlassTuningValue = (field: keyof GlassTuningSettings, value: number) => {
    setGlassTuning((prev) => ({ ...prev, [field]: value }));
  };

  const markClientUiReady = () => {
    if (clientReadySentRef.current) {
      return;
    }

    clientReadySentRef.current = true;
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        photinoBridge.sendCommand('client.uiReady');
      });
    });
  };

  useEffect(() => {
    return photinoBridge.subscribe((message) => {
      if (message.type === 'snapshot' && message.data) {
        startTransition(() => {
          setHostSnapshot(message.data ?? null);
          setRuntimeError(null);
        });
        return;
      }

      if (message.type === 'error') {
        setRuntimeError(message.message ?? 'Не удалось обработать команду shell.');
      }
    });
  }, []);

  useEffect(() => {
    const userAgent = window.navigator.userAgent.toLowerCase();
    const platformClass = userAgent.includes('mac os') || userAgent.includes('macintosh')
      ? 'platform-macos'
      : userAgent.includes('linux')
        ? 'platform-linux'
        : 'platform-windows';

    document.documentElement.classList.add(platformClass);
    return () => document.documentElement.classList.remove(platformClass);
  }, []);

  useEffect(() => {
    photinoBridge.requestSnapshot();
    const intervalId = window.setInterval(() => photinoBridge.requestSnapshot(), 1200);
    return () => window.clearInterval(intervalId);
  }, []);

  useEffect(() => {
    if (!launcher || accountDirty) return;
    setAccountForm((prev) => ({
      mode: account.mode === 'summary' ? prev.mode : account.mode ?? 'login',
      username: account.nicknameInput ?? '',
      password: ''
    }));
  }, [launcher, account.mode, account.nicknameInput, accountDirty]);

  useEffect(() => {
    if (!launcher || friendDirty) return;
    setFriendDraft(friends.friendNicknameInput ?? '');
  }, [launcher, friends.friendNicknameInput, friendDirty]);

  useEffect(() => {
    if (!launcher || javaPathDirty) return;
    setJavaPathDraft(settings.javaPath ?? '');
  }, [launcher, settings.javaPath, javaPathDirty]);

  useEffect(() => {
    if (!launcher || jvmArgsDirty) return;
    setJvmArgsDraft(settings.extraJvmArgs ?? '');
  }, [launcher, settings.extraJvmArgs, jvmArgsDirty]);

  useEffect(() => {
    if (!versionMenuOpen) return;

    const closeOnOutsidePointer = (event: PointerEvent) => {
      if ((event.target as HTMLElement | null)?.closest('.version-picker')) {
        return;
      }

      setVersionMenuOpen(false);
    };

    const closeOnEscape = (event: globalThis.KeyboardEvent) => {
      if (event.key === 'Escape') {
        setVersionMenuOpen(false);
      }
    };

    window.addEventListener('pointerdown', closeOnOutsidePointer);
    window.addEventListener('keydown', closeOnEscape);
    return () => {
      window.removeEventListener('pointerdown', closeOnOutsidePointer);
      window.removeEventListener('keydown', closeOnEscape);
    };
  }, [versionMenuOpen]);

  useLayoutEffect(() => {
    if (!versionMenuOpen) return;

    const updateVersionMenuGeometry = () => {
      const button = versionPickerButtonRef.current;
      if (!button) {
        return;
      }

      const rect = button.getBoundingClientRect();
      const viewportPadding = 10;
      const availableHeight = Math.max(80, window.innerHeight - rect.bottom - viewportPadding);

      setVersionMenuStyle({
        left: `${rect.left}px`,
        top: `${rect.bottom - 1}px`,
        width: `${rect.width}px`,
        maxHeight: `${availableHeight}px`
      });
    };

    updateVersionMenuGeometry();
    window.addEventListener('resize', updateVersionMenuGeometry);
    return () => window.removeEventListener('resize', updateVersionMenuGeometry);
  }, [versionMenuOpen]);

  useEffect(() => {
    if (loadingState) return;

    let cancelled = false;
    const readyFallbackId = window.setTimeout(() => {
      if (!cancelled) {
        markClientUiReady();
      }
    }, 8000);

    if (!settingsLiquidButtonRef.current) {
      markClientUiReady();
      return () => {
        cancelled = true;
        window.clearTimeout(readyFallbackId);
      };
    }

    const loadScript = (src: string, id: string) => new Promise<void>((resolve, reject) => {
      const existingScript = document.querySelector<HTMLScriptElement>(`script[data-vesper-vendor="${id}"]`);
      if (existingScript) {
        if (existingScript.dataset.loaded === 'true') {
          resolve();
          return;
        }

        existingScript.addEventListener('load', () => resolve(), { once: true });
        existingScript.addEventListener('error', () => reject(new Error(`Не удалось загрузить ${id}`)), { once: true });
        return;
      }

      const script = document.createElement('script');
      script.src = src;
      script.async = false;
      script.dataset.vesperVendor = id;
      script.addEventListener('load', () => {
        script.dataset.loaded = 'true';
        resolve();
      }, { once: true });
      script.addEventListener('error', () => reject(new Error(`Не удалось загрузить ${id}`)), { once: true });
      document.body.appendChild(script);
    });

    const initializeLiquidSettingsButton = async () => {
      try {
        await loadScript(html2canvasScriptUrl, 'html2canvas');
        await loadScript(liquidGlScriptUrl, 'liquidgl');
        await new Promise<void>((resolve) => window.requestAnimationFrame(() => resolve()));

        if (cancelled || !settingsLiquidButtonRef.current || !window.liquidGL) {
          markClientUiReady();
          return;
        }

        const targets = Array.from(document.querySelectorAll<HTMLElement>('.liquid-glass-layer:not([data-liquid-initialized])'))
          .filter((element) => element.offsetWidth > 0 && element.offsetHeight > 0);

        if (!targets.length) {
          markClientUiReady();
          return;
        }

        const batchId = `vesper-liquid-${++liquidGlassBatchRef.current}`;
        targets.forEach((element) => {
          element.dataset.liquidInitialized = 'true';
          element.dataset.liquidBatch = batchId;
        });

        window.liquidGL({
          target: `[data-liquid-batch="${batchId}"]`,
          snapshot: '.liquid-glass-snapshot-source',
          resolution: glassTuning.resolution,
          refraction: glassTuning.refraction,
          bevelDepth: glassTuning.bevelDepth,
          bevelWidth: glassTuning.bevelWidth,
          frost: glassTuning.frost,
          shadow: false,
          specular: false,
          reveal: 'none',
          tilt: false,
          magnify: 1
        });

        await new Promise<void>((resolve) => window.setTimeout(resolve, 650));
        markClientUiReady();
      } catch (error) {
        console.warn('liquidGL controls test failed', error);
        markClientUiReady();
      }
    };

    initializeLiquidSettingsButton();
    return () => {
      cancelled = true;
      window.clearTimeout(readyFallbackId);
    };
  }, [loadingState, activeSection, versionMenuOpen]);

  useEffect(() => {
    const renderer = (window as any).__liquidGLRenderer__;
    if (!renderer?.lenses) return;

    renderer.lenses.forEach((lens: any) => {
      lens.options.refraction = glassTuning.refraction;
      lens.options.bevelDepth = glassTuning.bevelDepth;
      lens.options.bevelWidth = glassTuning.bevelWidth;
      lens.options.frost = glassTuning.frost;
    });

    if (liquidGlassResolutionRef.current !== glassTuning.resolution) {
      liquidGlassResolutionRef.current = glassTuning.resolution;
      renderer._snapshotResolution = glassTuning.resolution;
      renderer.captureSnapshot?.();
      return;
    }

    renderer.render?.();
  }, [glassTuning]);

  const pageBackground = theme.backgroundUrl
    ? `url(${theme.backgroundUrl}) center / cover no-repeat`
    : 'radial-gradient(circle at top, rgba(111, 144, 166, 0.18) 0%, rgba(10, 12, 15, 0) 40%), linear-gradient(180deg, #0b0e11 0%, #060709 100%)';

  const openSection = (sectionId: string) => {
    if (!launcher) return;
    if (launcher.activeSection === sectionId) {
      photinoBridge.sendCommand('shell.closeSection');
      return;
    }
    photinoBridge.sendCommand('shell.openSection', { section: sectionId });
  };

  const stopButtonPointer = (event: ReactPointerEvent<HTMLElement>) => {
    event.stopPropagation();
  };

  const runButtonAction = (event: ReactPointerEvent<HTMLElement>, action: () => void) => {
    if (event.button !== 0) return;
    event.preventDefault();
    event.stopPropagation();
    action();
  };

  const startWindowDrag = (event: MouseEvent<HTMLElement>) => {
    if (event.button !== 0 || event.detail > 1) return;
    if ((event.target as HTMLElement).closest('button, input, select, textarea, a')) return;
    photinoBridge.sendCommand('host.startDrag');
  };

  const submitAccount = () => {
    setAccountDirty(false);
    photinoBridge.sendCommand('account.submit', {
      mode: accountForm.mode,
      username: accountForm.username,
      password: accountForm.password
    });
    setAccountForm((prev) => ({ ...prev, password: '' }));
  };

  const toggleSelectedMod = (projectId: string) => {
    const selected = new Set<string>((mods.selectedProjectIds ?? []) as string[]);
    if (selected.has(projectId)) selected.delete(projectId);
    else selected.add(projectId);
    photinoBridge.sendCommand('mods.setSelectedProjects', { projectIds: Array.from(selected) });
  };

  const versionOptions = (main.availableVersions ?? []) as Array<Record<string, any>>;
  const selectedVersionKey = String(main.selectedVersionKey ?? '');
  const selectedVersion = versionOptions.find((entry) => String(entry.key ?? '') === selectedVersionKey);
  const selectedVersionLabel = selectedVersion?.displayName ?? main.inlineVersionLabel ?? '\u0412\u0435\u0440\u0441\u0438\u044f: \u043d\u0435 \u0432\u044b\u0431\u0440\u0430\u043d\u0430';
  const progressPercent = main.progressPercent ?? hostSnapshot?.update?.progressPercent ?? 0;
  const rightPanelVisible = !loadingState && activeSection !== 'none';
  const isWideSidePanel = !loadingState && WPF_WIDE_PANEL_SECTIONS.has(activeSection);
  const isSettingsWidthSidePanel = !loadingState && SETTINGS_WIDTH_PANEL_SECTIONS.has(activeSection);
  const isLauncherSettingsPanel = !loadingState && activeSection === 'settings';

  const panelProps: PanelRenderProps | null = launcher
    ? {
        launcher, accountForm, setAccountForm, setAccountDirty, submitAccount,
        friendDraft, setFriendDraft, setFriendDirty,
        javaPathDraft, setJavaPathDraft, setJavaPathDirty,
        jvmArgsDraft, setJvmArgsDraft, setJvmArgsDirty,
        toggleSelectedMod,
        glassTuning, setGlassTuningValue
      }
    : null;

  return (
    <div className="app-shell" style={{ background: pageBackground }} key={theme.backgroundUrl ?? 'default'}>
      <div className="liquid-glass-snapshot-source" style={{ background: pageBackground }} aria-hidden="true" />
      <div className="procedural-background" />
      <div className="background-vignette" />

      {loadingState ? (
        <LoadingScreen hostSnapshot={hostSnapshot} runtimeError={runtimeError} />
      ) : (
        <div className="launcher-frame">
        <header className="titlebar" onMouseDown={startWindowDrag} onDoubleClick={() => photinoBridge.sendCommand('host.toggleMaximize')}>
          <div className="titlebar-brand">
            {theme.logoUrl ? <img className="brand-logo" src={theme.logoUrl} alt="Vesper" /> : null}
            <span>Vesper Launcher</span>
          </div>

          <div className="titlebar-actions">
            <button className="title-icon-button notifications-button" onPointerDown={stopButtonPointer} onPointerUp={(event) => runButtonAction(event, () => openSection('friends'))} onClick={(event) => event.preventDefault()} type="button" title="Приглашения в друзья">
              <span className="mdl-icon">&#xE7F4;</span>
              {launcher?.notificationsCount ? <strong className="notification-badge">{launcher.notificationsCount}</strong> : null}
            </button>
            <button className="title-account-button" onPointerDown={stopButtonPointer} onPointerUp={(event) => runButtonAction(event, () => openSection('account'))} onClick={(event) => event.preventDefault()} type="button">
              {account.currentNickname || main.nickname || 'Создайте аккаунт'}
            </button>
            <button className="title-icon-button" onPointerDown={stopButtonPointer} onPointerUp={(event) => runButtonAction(event, () => photinoBridge.sendCommand('host.minimize'))} onClick={(event) => event.preventDefault()} type="button">−</button>
            <button className="title-icon-button" onPointerDown={stopButtonPointer} onPointerUp={(event) => runButtonAction(event, () => photinoBridge.sendCommand('host.close'))} onClick={(event) => event.preventDefault()} type="button">✕</button>
          </div>
        </header>

        <div className="classic-workspace">
            <div className="classic-main-grid">
              <aside className="left-control-surface">
                <div className="left-control-surface-bg glass-panel" />
                <div className="left-panel-content">
                  <div className="left-panel-top">
                    {theme.wordmarkUrl ? <img className="left-wordmark" src={theme.wordmarkUrl} alt="Vesper Launcher" /> : null}
                    <p className="launcher-subtitle">Minecraft Java Launcher</p>

                    <div className="classic-field-block">
                      <label className="classic-label">Игрок</label>
                      <div className="classic-inline-row">
                        <button className="static-glass-control text-left left-liquid-glass-button" data-liquid-label={account.currentNickname || main.nickname || main.usernameText || 'Создайте аккаунт'} onPointerDown={stopButtonPointer} onPointerUp={(event) => runButtonAction(event, () => openSection('account'))} onClick={(event) => event.preventDefault()} type="button">
                          <span className="left-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                          <span className="left-liquid-glass-content">{account.currentNickname || main.nickname || main.usernameText || 'Создайте аккаунт'}</span>
                        </button>
                        <button className="input-icon-button left-liquid-glass-button" data-liquid-icon={'\uE77B'} onPointerDown={stopButtonPointer} onPointerUp={(event) => runButtonAction(event, () => openSection('account'))} onClick={(event) => event.preventDefault()} type="button" title="Меню игрока">
                          <span className="left-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                          <span className="mdl-icon left-liquid-glass-content">&#xE77B;</span>
                        </button>
                      </div>
                    </div>

                    <div className="classic-field-block version-block">
                      <div className="classic-inline-row">
                        <div className={`version-picker ${versionMenuOpen ? 'open' : ''}`}>
                          <button
                            ref={versionPickerButtonRef}
                            className="version-picker-toggle left-liquid-glass-button"
                            data-liquid-label={selectedVersionLabel}
                            type="button"
                            aria-haspopup="listbox"
                            aria-expanded={versionMenuOpen}
                            onPointerDown={stopButtonPointer}
                            onPointerUp={(event) => runButtonAction(event, () => setVersionMenuOpen((open) => !open))}
                            onClick={(event) => event.preventDefault()}
                          >
                            <span className="left-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                            <span className="version-picker-label left-liquid-glass-content">{selectedVersionLabel}</span>
                            <span className="select-chevron left-liquid-glass-content">&#xE70D;</span>
                          </button>

                          {versionMenuOpen ? (
                            <div className="version-picker-menu" role="listbox" style={versionMenuStyle}>
                              <span className="version-picker-menu-liquid-layer left-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                              {versionOptions.map((entry) => {
                                const key = String(entry.key ?? '');
                                const isSelected = key === selectedVersionKey;

                                return (
                                  <button
                                    key={key}
                                    className={`version-picker-option ${isSelected ? 'selected' : ''}`}
                                    type="button"
                                    role="option"
                                    aria-selected={isSelected}
                                    onPointerDown={stopButtonPointer}
                                    onPointerUp={(event) => runButtonAction(event, () => {
                                      setVersionMenuOpen(false);
                                      photinoBridge.sendCommand('main.selectVersionKey', { key });
                                    })}
                                    onClick={(event) => event.preventDefault()}
                                  >
                                    <span>{entry.displayName}</span>
                                  </button>
                                );
                              })}
                            </div>
                          ) : null}
                        </div>

                        <div className="select-shell static-glass-control">
                          <select value={main.selectedVersionKey ?? ''} onChange={(e) => photinoBridge.sendCommand('main.selectVersionKey', { key: e.target.value })}>
                            <option value="">{main.inlineVersionLabel || 'Версия: не выбрана'}</option>
                            {versionOptions.map((entry) => (
                              <option key={String(entry.key)} value={String(entry.key)}>{entry.displayName}</option>
                            ))}
                          </select>
                          <span className="select-chevron">&#xE70D;</span>
                        </div>
                        <button className="input-icon-button left-liquid-glass-button" data-liquid-icon={'\uE8B7'} onPointerDown={stopButtonPointer} onPointerUp={(event) => runButtonAction(event, () => photinoBridge.sendCommand('main.openProfileFolder'))} onClick={(event) => event.preventDefault()} type="button" title="Папка профиля">
                          <span className="left-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                          <span className="mdl-icon left-liquid-glass-content">&#xE8B7;</span>
                        </button>
                      </div>

                      <div className="classic-inline-row launch-row-classic">
                        <button className="main-glass-button launch-button-classic left-liquid-glass-button" data-liquid-label={main.launchButtonText || 'Играть'} onPointerDown={stopButtonPointer} onPointerUp={(event) => runButtonAction(event, () => photinoBridge.sendCommand('main.launch'))} onClick={(event) => event.preventDefault()} type="button">
                          <span className="left-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                          <span className="launch-button-progress" style={{ width: `${progressPercent}%` }} />
                          <span className="left-liquid-glass-content">{main.launchButtonText || 'Играть'}</span>
                        </button>
                        <button ref={settingsLiquidButtonRef} className="input-icon-button settings-liquid-button left-liquid-glass-button" data-liquid-ignore="" data-liquid-icon={'\uE713'} onPointerDown={stopButtonPointer} onPointerUp={(event) => runButtonAction(event, () => openSection('settings'))} onClick={(event) => event.preventDefault()} type="button" title="Настройки">
                          <span className="settings-liquid-glass-test left-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                          <span className="mdl-icon settings-liquid-button-icon left-liquid-glass-content">&#xE713;</span>
                        </button>
                      </div>
                    </div>
                  </div>

                  <div className="left-panel-bottom">
                    <p className="status-text">{main.statusText || runtimeError || hostSnapshot?.update?.message || 'Готов к запуску'}</p>
                    <p className="progress-text">{main.progressText || hostSnapshot?.update?.detailMessage || 'Ожидание...'}</p>
                    <div className="classic-progress">
                      <div className={`classic-progress-fill ${main.isProgressIndeterminate ? 'indeterminate' : ''}`} style={{ width: `${progressPercent}%` }} />
                      <span>{main.progressOverlayText || hostSnapshot?.update?.progressText || ''}</span>
                    </div>
                  </div>
                </div>
              </aside>

              <aside className={`sidepanel classic-side-panel glass-panel ${rightPanelVisible ? 'visible' : ''} ${isWideSidePanel ? 'wide-section-panel' : ''} ${isSettingsWidthSidePanel ? 'settings-section-panel' : ''} ${isLauncherSettingsPanel ? 'launcher-settings-side-panel' : ''}`}>
                {panelProps ? renderPanel(panelProps) : null}
              </aside>

              <nav className="bottom-action-bar">
                <div className="bottom-action-bar-bg glass-panel" />
                {BOTTOM_SECTION_ITEMS.map((section) => (
                  <button
                    key={section.id}
                    className={`main-glass-button ${launcher?.activeSection === section.id ? 'active' : ''} left-liquid-glass-button`}
                    data-liquid-label={section.label}
                    onPointerDown={stopButtonPointer}
                    onPointerUp={(event) => runButtonAction(event, () => openSection(section.id))}
                    onClick={(event) => event.preventDefault()}
                    type="button"
                  >
                    <span className="left-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                    <span className="left-liquid-glass-content">{section.label}</span>
                  </button>
                ))}
              </nav>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function renderPanel(props: PanelRenderProps) {
  switch (props.launcher.activeSection) {
    case 'account': return <AccountPanel {...props} />;
    case 'settings': return <SettingsPanel {...props} />;
    case 'skin': return <SkinPanel {...props} />;
    case 'background': return <BackgroundPanel {...props} />;
    case 'mods': return <ModsPanel {...props} />;
    case 'friends': return <FriendsPanel {...props} />;
    default: return <EmptyPanel />;
  }
}

export default App;

