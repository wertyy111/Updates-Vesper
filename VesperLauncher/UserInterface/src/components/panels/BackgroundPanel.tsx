import { photinoBridge } from '../../bridge';
import type { PanelRenderProps } from '../../types';
import { PanelHeader } from '../common/PanelHeader';

const getDynamicBackgroundUrl = () => {
  const hour = new Date().getHours();
  if (hour >= 6 && hour < 12) return '/launcher-assets/bg-sunrise.png';
  if (hour >= 12 && hour < 18) return '/launcher-assets/bg-day.png';
  if (hour >= 18 && hour < 22) return '/launcher-assets/bg-sunset.png';
  return '/launcher-assets/bg-night.png';
};

export function BackgroundPanel({ launcher }: PanelRenderProps) {
  const background = launcher.background;
  const items = (background.items ?? []) as Array<Record<string, any>>;
  const isProceduralActive = !background.appliedBackgroundUrl;
  const currentDynamicBg = getDynamicBackgroundUrl();

  return (
    <>
      <PanelHeader title="Фон" subtitle="Выберите фон интерфейса лаунчера или загрузите свой." />

      <section className="background-shell">
        <div className="background-catalog-header" style={{ marginBottom: '16px' }}>
          <button
            className="subtle-button left-liquid-glass-button settings-liquid-glass-button rounded-pill"
            onClick={() => photinoBridge.sendCommand('background.openFolder')}
            type="button"
          >
            <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
            <span className="left-liquid-glass-content">Папка с фонами</span>
          </button>
        </div>

        <div className="background-catalog-grid">
          {/* Top-left: Standard procedural background (represented by dynamic sunrise/day/sunset/night bg) */}
          <div
            className={`background-catalog-item ${isProceduralActive ? 'selected' : ''}`}
            onClick={() => photinoBridge.sendCommand('background.reset')}
            role="button"
            tabIndex={0}
          >
            <img src={currentDynamicBg} alt="Standard procedural background" />
            
            {/* Clock icon badge for dynamic time of day changing */}
            <div className="dynamic-time-badge" title="Смена фона по времени суток">
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                <circle cx="12" cy="12" r="10" />
                <polyline points="12 6 12 12 16 14" />
              </svg>
              <span>Динамический</span>
            </div>

            {isProceduralActive && (
              <div className="selected-badge">Выбрано</div>
            )}
            <span>Standard procedural background</span>
          </div>

          {/* Custom backgrounds */}
          {items.map((item) => (
            <div
              key={String(item.fileName)}
              className={`background-catalog-item ${item.isActive ? 'selected' : ''}`}
              onClick={() => photinoBridge.sendCommand('background.setPreset', { fileName: item.fileName })}
              role="button"
              tabIndex={0}
            >
              {item.url ? (
                <img src={item.url} alt={String(item.label)} />
              ) : (
                <img src={currentDynamicBg} alt={String(item.label || item.fileName)} />
              )}
              {item.isActive && (
                <div className="selected-badge">Выбрано</div>
              )}
              <span>{item.label || item.fileName}</span>
            </div>
          ))}
        </div>
      </section>
    </>
  );
}
