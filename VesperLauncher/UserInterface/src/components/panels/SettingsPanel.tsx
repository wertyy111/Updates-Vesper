import { useEffect, useState, type CSSProperties } from 'react';
import { photinoBridge } from '../../bridge';
import type { PanelRenderProps } from '../../types';
import { PanelHeader } from '../common/PanelHeader';
import { ToggleCard } from '../common/ToggleCard';
import { InlineSelectField } from '../common/SelectFields';

const SETTINGS_TAB_ITEMS = [
  { id: 'launcher', label: 'Лаунчер' },
  { id: 'java', label: 'Java' },
  { id: 'vesper', label: 'Vesper' },
  { id: 'launch', label: 'Запуск' },
  { id: 'language', label: 'Язык' },
  { id: 'glass', label: 'Стекло' }
];

type GlassSettingField = keyof PanelRenderProps['glassTuning'];

const GLASS_SETTING_ITEMS: Array<{ field: GlassSettingField; label: string; min: number; max: number; step: number; digits: number }> = [
  { field: 'refraction', label: 'Преломление', min: 0, max: 0.02, step: 0.001, digits: 3 },
  { field: 'bevelDepth', label: 'Глубина', min: 0, max: 0.16, step: 0.01, digits: 2 },
  { field: 'bevelWidth', label: 'Край', min: 0.03, max: 0.28, step: 0.01, digits: 2 },
  { field: 'frost', label: 'Мягкость', min: 0, max: 1.5, step: 0.05, digits: 2 },
  { field: 'resolution', label: 'Качество', min: 1, max: 3, step: 0.5, digits: 1 }
];

const getRangeStyle = (value: number, min: number, max: number) => ({
  '--range-progress': `${((value - min) / Math.max(max - min, 1)) * 100}%`
}) as CSSProperties;

export function SettingsPanel({ launcher, javaPathDraft, setJavaPathDraft, setJavaPathDirty, jvmArgsDraft, setJvmArgsDraft, setJvmArgsDirty, glassTuning, setGlassTuningValue }: PanelRenderProps) {
  const settings = launcher.settings;
  const activeTab = settings.activeTab ?? 'launcher';
  const [memoryDraftMb, setMemoryDraftMb] = useState<number | null>(null);
  const minimumMemoryMb = Number(settings.minimumMemoryMb ?? 2048);
  const maximumMemoryMb = Number(settings.maximumMemoryMb ?? 12288);
  const storedMemoryMb = Number(settings.memoryMb ?? 4096);
  const displayedMemoryMb = Number(settings.displayedMemoryMb ?? storedMemoryMb);
  const isAutoMemoryEnabled = Boolean(settings.autoOptimizeMemory);
  const clampMemoryMb = (value: number) => Math.min(Math.max(value, minimumMemoryMb), maximumMemoryMb);
  const memorySliderValue = clampMemoryMb(isAutoMemoryEnabled ? displayedMemoryMb : memoryDraftMb ?? storedMemoryMb);
  const memoryLabelMb = isAutoMemoryEnabled ? displayedMemoryMb : memorySliderValue;
  const memoryRange = Math.max(maximumMemoryMb - minimumMemoryMb, 1);
  const memoryRangeProgress = ((memorySliderValue - minimumMemoryMb) / memoryRange) * 100;
  const memoryRangeStyle = { '--range-progress': `${memoryRangeProgress}%` } as CSSProperties;
  const pageClass = (tabId: string) => `wpf-settings-page settings-tab-page ${activeTab === tabId ? 'active' : ''}`;
  const isPageHidden = (tabId: string) => activeTab !== tabId;
  const commitMemoryMb = (value: number) => {
    if (isAutoMemoryEnabled) return;

    const normalizedValue = clampMemoryMb(value);
    setMemoryDraftMb(normalizedValue);
    photinoBridge.sendCommand('settings.setMemory', { value: normalizedValue });
  };

  useEffect(() => {
    if (isAutoMemoryEnabled) {
      setMemoryDraftMb(null);
      return;
    }

    if (memoryDraftMb !== null && memoryDraftMb === storedMemoryMb) {
      setMemoryDraftMb(null);
    }
  }, [isAutoMemoryEnabled, memoryDraftMb, storedMemoryMb]);

  return (
    <>
      <PanelHeader title="Настройки лаунчера" subtitle="Только запуск, Java, память и функции Vesper." />

      <section className="wpf-section-shell settings-shell">
        <div className="tab-row compact wpf-tabs">
          {SETTINGS_TAB_ITEMS.map((tab) => (
            <button key={tab.id} className={`tab-button left-liquid-glass-button settings-liquid-glass-button ${activeTab === tab.id ? 'active' : ''}`} onClick={() => photinoBridge.sendCommand('settings.selectTab', { tabId: tab.id })} type="button">
              <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
              <span className="left-liquid-glass-content">{tab.label}</span>
            </button>
          ))}
        </div>

        <div className="settings-pages-stack">
          <div className={pageClass('launcher')} aria-hidden={isPageHidden('launcher')}>
            <h3 className="settings-page-title">Лаунчер</h3>
            <InlineSelectField label="Форма входа:" value={settings.loginFormPlacementId ?? 'center'} options={(settings.loginPlacementOptions ?? []) as Array<Record<string, any>>} onChange={(value) => photinoBridge.sendCommand('settings.setOption', { field: 'loginFormPlacementId', value })} />
            <div className="wpf-form-row">
              <span className="wpf-row-label">Java / JRE:</span>
              <select className="launcher-select" value={settings.javaRuntimeMode ?? 'system'} onChange={(event) => photinoBridge.sendCommand('settings.setOption', { field: 'javaRuntimeMode', value: event.target.value })}>
                {((settings.javaRuntimeOptions ?? []) as Array<Record<string, any>>).map((option) => <option key={String(option.id)} value={String(option.id)}>{option.label}</option>)}
              </select>
              <button className="subtle-button left-liquid-glass-button settings-liquid-glass-button" onClick={() => photinoBridge.sendCommand('settings.selectTab', { tabId: 'java' })} type="button">
                <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                <span className="left-liquid-glass-content">Настроить...</span>
              </button>
            </div>
            <div className="wpf-form-row">
              <span className="wpf-row-label">Папка игры:</span>
              <input className="launcher-input" readOnly value={settings.displayedGameDirectory ?? ''} />
              <button className="subtle-button left-liquid-glass-button settings-liquid-glass-button" onClick={() => photinoBridge.sendCommand('settings.openGameDirectory')} type="button">
                <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                <span className="left-liquid-glass-content">Открыть...</span>
              </button>
            </div>
            <InlineSelectField label="Папки:" value={settings.launcherDirectoryViewId ?? 'current'} options={(settings.directoryViewOptions ?? []) as Array<Record<string, any>>} onChange={(value) => photinoBridge.sendCommand('settings.setOption', { field: 'launcherDirectoryViewId', value })} />
          </div>

          <div className={pageClass('java')} aria-hidden={isPageHidden('java')}>
            <h3 className="settings-page-title">Java</h3>
            <div className="settings-toggle-row">
              <div>
                <strong>Использовать системную Java</strong>
                <p>{settings.javaModeHint}</p>
              </div>
              <div className={`settings-segmented-toggle left-liquid-glass-button settings-liquid-glass-button ${settings.useSystemJava ? 'is-left-active' : 'is-right-active'}`} role="group" aria-label="Режим Java">
                <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                <button className={settings.useSystemJava ? 'active' : ''} onClick={() => photinoBridge.sendCommand('settings.setToggle', { field: 'useSystemJava', value: true })} type="button">Система</button>
                <button className={!settings.useSystemJava ? 'active' : ''} onClick={() => photinoBridge.sendCommand('settings.setToggle', { field: 'useSystemJava', value: false })} type="button">Свой</button>
              </div>
            </div>
            <label className="field-label">
              Путь к Java
              <input
                className="launcher-input"
                value={javaPathDraft}
                onChange={(event) => {
                  setJavaPathDirty(true);
                  setJavaPathDraft(event.target.value);
                }}
                onBlur={() => {
                  setJavaPathDirty(false);
                  photinoBridge.sendCommand('settings.setText', { field: 'javaPath', value: javaPathDraft });
                }}
              />
            </label>
          </div>

          <div className={pageClass('vesper')} aria-hidden={isPageHidden('vesper')}>
            <h3 className="settings-page-title">Vesper</h3>
            <ToggleCard label="Свернуть после запуска" hint={settings.autoMinimizeHint} checked={Boolean(settings.autoMinimizeOnLaunch)} onChange={(value) => photinoBridge.sendCommand('settings.setToggle', { field: 'autoMinimizeOnLaunch', value })} />
            <ToggleCard label="Вернуть окно после выхода" hint={settings.restoreHint} checked={Boolean(settings.restoreLauncherAfterGameExit)} onChange={(value) => photinoBridge.sendCommand('settings.setToggle', { field: 'restoreLauncherAfterGameExit', value })} />
            <ToggleCard label="Звуки интерфейса" hint="Звуки кнопок лаунчера." checked={Boolean(settings.clickSoundEnabled)} onChange={(value) => photinoBridge.sendCommand('settings.setToggle', { field: 'clickSoundEnabled', value })} />
          </div>

          <div className={pageClass('launch')} aria-hidden={isPageHidden('launch')}>
            <h3 className="settings-page-title">Запуск</h3>
            <ToggleCard label="Авто память" hint={settings.autoMemoryHint} checked={isAutoMemoryEnabled} onChange={(value) => photinoBridge.sendCommand('settings.setToggle', { field: 'autoOptimizeMemory', value })} />
            <label className="field-label">
              Память: {memoryLabelMb} MB
              <input
                className="launcher-range"
                disabled={isAutoMemoryEnabled}
                min={minimumMemoryMb}
                max={maximumMemoryMb}
                step={256}
                style={memoryRangeStyle}
                type="range"
                value={memorySliderValue}
                onBlur={(event) => {
                  if (memoryDraftMb !== null) commitMemoryMb(Number(event.currentTarget.value));
                }}
                onChange={(event) => setMemoryDraftMb(clampMemoryMb(Number(event.currentTarget.value)))}
                onKeyUp={(event) => commitMemoryMb(Number(event.currentTarget.value))}
                onPointerUp={(event) => commitMemoryMb(Number(event.currentTarget.value))}
              />
            </label>
            <div className="settings-presets-row">
              {[4096, 6144, 8192].map((value) => (
                <button key={value} className={`chip-button left-liquid-glass-button settings-liquid-glass-button ${storedMemoryMb === value ? 'active' : ''}`} disabled={isAutoMemoryEnabled} onClick={() => commitMemoryMb(value)} type="button">
                  <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                  <span className="left-liquid-glass-content">{value / 1024} GB</span>
                </button>
              ))}
            </div>
            <ToggleCard label="Дополнительные JVM аргументы" hint={settings.jvmArgsHint} checked={Boolean(settings.showJvmArgs)} onChange={(value) => photinoBridge.sendCommand('settings.setToggle', { field: 'showJvmArgs', value })} />
            {settings.showJvmArgs || jvmArgsDraft ? (
              <label className="field-label jvm-args-card">
                Аргументы JVM
                <textarea
                  className="launcher-textarea"
                  value={jvmArgsDraft}
                  onChange={(event) => {
                    setJvmArgsDirty(true);
                    setJvmArgsDraft(event.target.value);
                  }}
                  onBlur={() => {
                    setJvmArgsDirty(false);
                    photinoBridge.sendCommand('settings.setText', { field: 'extraJvmArgs', value: jvmArgsDraft });
                  }}
                />
              </label>
            ) : null}
          </div>

          <div className={pageClass('language')} aria-hidden={isPageHidden('language')}>
            <h3 className="settings-page-title">Язык</h3>
            <InlineSelectField label="Язык Minecraft" value={settings.minecraftLanguageCode ?? 'auto'} options={(settings.languageOptions ?? []) as Array<Record<string, any>>} onChange={(value) => photinoBridge.sendCommand('settings.setOption', { field: 'minecraftLanguageCode', value })} />
          </div>

          <div className={pageClass('glass')} aria-hidden={isPageHidden('glass')}>
            <h3 className="settings-page-title">Стекло</h3>
            <div className="glass-settings-list">
              {GLASS_SETTING_ITEMS.map((item) => {
                const value = glassTuning[item.field];

                return (
                  <label className="field-label glass-settings-row" key={item.field}>
                    <span>{item.label}: {value.toFixed(item.digits)}</span>
                    <input
                      className="launcher-range"
                      min={item.min}
                      max={item.max}
                      step={item.step}
                      style={getRangeStyle(value, item.min, item.max)}
                      type="range"
                      value={value}
                      onChange={(event) => setGlassTuningValue(item.field, Number(event.currentTarget.value))}
                    />
                  </label>
                );
              })}
            </div>
          </div>
        </div>
      </section>
    </>
  );
}
