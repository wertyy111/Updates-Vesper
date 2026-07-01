import { useEffect, useRef } from 'react';
import { SkinViewer, WalkingAnimation } from 'skinview3d';
import { photinoBridge } from '../../bridge';
import type { PanelRenderProps } from '../../types';
import { PanelHeader } from '../common/PanelHeader';

interface Skin3DPreviewProps {
  skinUrl: string;
  isSlim: boolean;
}

function Skin3DPreview({ skinUrl, isSlim }: Skin3DPreviewProps) {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    if (!canvasRef.current || !skinUrl) return;

    try {
      const viewer = new SkinViewer({
        canvas: canvasRef.current,
        width: 250,
        height: 370,
        skin: skinUrl,
        model: isSlim ? 'slim' : 'default'
      });

      viewer.controls.enableZoom = false;
      viewer.controls.enablePan = false;

      viewer.autoRotate = false;

      viewer.animation = new WalkingAnimation();
      viewer.animation.speed = 0.55;

      return () => {
        viewer.dispose();
      };
    } catch (e) {
      console.error("Failed to render 3D skin:", e);
    }
  }, [skinUrl, isSlim]);

  return (
    <canvas
      ref={canvasRef}
      className="skin-preview-rendered"
      style={{ outline: 'none' }}
    />
  );
}

export function SkinPanel({ launcher }: PanelRenderProps) {
  const skin = launcher.skin;
  const selectedSkinUrl = typeof skin.selectedSkinUrl === 'string' && skin.selectedSkinUrl.trim().length > 0
    ? skin.selectedSkinUrl
    : '';
  const isSlim = !!skin.selectedSkinIsSlim;
  const modelPreferenceId = skin.modelPreferenceId ?? 'auto';

  return (
    <>
      <PanelHeader title="Скин" subtitle={skin.selectedSkinLabel || 'Скин не выбран.'} />

      <section className="skin-shell">
        <div className="skin-wpf-layout">
          <div className="skin-wpf-preview">
            {selectedSkinUrl ? (
              <Skin3DPreview skinUrl={selectedSkinUrl} isSlim={isSlim} />
            ) : (
              <div className="empty-preview">PNG не выбран</div>
            )}
          </div>

          <div className="skin-wpf-controls">
            <div className="skin-control-row">
              <strong>Скин персонажа</strong>
              <p>Загрузите свой PNG-файл скина или сбросьте его до стандартного.</p>
              
              <div className="skin-action-buttons">
                <button
                  className="subtle-button left-liquid-glass-button settings-liquid-glass-button rounded-pill"
                  onClick={() => photinoBridge.sendCommand('skin.importDialog')}
                  type="button"
                >
                  <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                  <span className="left-liquid-glass-content">Импорт</span>
                </button>
                
                <button
                  className="danger-button left-liquid-glass-button settings-liquid-glass-button rounded-pill"
                  onClick={() => photinoBridge.sendCommand('skin.clear')}
                  type="button"
                >
                  <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                  <span className="left-liquid-glass-content">Сбросить</span>
                </button>
              </div>
            </div>

            <div className="skin-control-row model-selection">
              <strong>Модель игрока</strong>
              <p>Выберите тип геометрии рук персонажа.</p>
              
              <div className={`skin-segmented-toggle left-liquid-glass-button settings-liquid-glass-button ${modelPreferenceId === 'classic' ? 'is-classic-active' : modelPreferenceId === 'slim' ? 'is-slim-active' : ''}`} role="group" aria-label="Модель">
                <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                <button
                  className={modelPreferenceId === 'auto' ? 'active' : ''}
                  onClick={() => photinoBridge.sendCommand('skin.setModel', { modelId: 'auto' })}
                  type="button"
                >
                  Авто
                </button>
                <button
                  className={modelPreferenceId === 'classic' ? 'active' : ''}
                  onClick={() => photinoBridge.sendCommand('skin.setModel', { modelId: 'classic' })}
                  type="button"
                >
                  Классик
                </button>
                <button
                  className={modelPreferenceId === 'slim' ? 'active' : ''}
                  onClick={() => photinoBridge.sendCommand('skin.setModel', { modelId: 'slim' })}
                  type="button"
                >
                  Слим
                </button>
              </div>
            </div>
          </div>
        </div>
      </section>
    </>
  );
}
