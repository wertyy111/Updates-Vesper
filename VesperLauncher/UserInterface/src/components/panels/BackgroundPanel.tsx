import { photinoBridge } from '../../bridge';
import type { PanelRenderProps } from '../../types';
import { PanelHeader } from '../common/PanelHeader';

export function BackgroundPanel({ launcher }: PanelRenderProps) {
  const background = launcher.background;
  const items = (background.items ?? []) as Array<Record<string, any>>;

  return (
    <>
      <PanelHeader title="Фон" subtitle="Фон лаунчера берется из той же папки, что и в WPF-версии." />

      <section className="wpf-section-shell background-shell">
        <div className="background-current-card glass-inset">
          <span>Текущий фон</span>
          <strong>{background.currentPresetLabel || 'Стандартный'}</strong>
        </div>

        <div className="visual-preview wide glass-inset">
          {background.appliedBackgroundUrl ? <img className="background-preview" src={background.appliedBackgroundUrl} alt="background" /> : <div className="empty-preview">Стандартный процедурный фон</div>}
        </div>

        <div className="panel-actions-grid two-columns">
          <button className="subtle-button" onClick={() => photinoBridge.sendCommand('background.reset')} type="button">Применить дефолт</button>
          <button className="subtle-button" onClick={() => photinoBridge.sendCommand('background.openFolder')} type="button">Папка</button>
        </div>

        {items.length > 0 ? (
          <div className="media-grid wide-tiles background-list">
            {items.map((item) => (
              <div key={String(item.fileName)} className={`media-card ${item.isActive ? 'selected' : ''}`} onClick={() => photinoBridge.sendCommand('background.setPreset', { fileName: item.fileName })}>
                {item.url ? <img src={item.url} alt={String(item.label)} /> : null}
                <span>{item.label}</span>
              </div>
            ))}
          </div>
        ) : null}
      </section>
    </>
  );
}
