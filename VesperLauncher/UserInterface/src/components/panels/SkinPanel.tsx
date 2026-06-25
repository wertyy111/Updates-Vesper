import { useEffect, useRef } from 'react';
import { SkinViewer, WalkingAnimation } from 'skinview3d';
import { photinoBridge } from '../../bridge';
import type { PanelRenderProps } from '../../types';
import { PanelHeader } from '../common/PanelHeader';
import { SelectField } from '../common/SelectFields';

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

      viewer.autoRotate = true;
      viewer.autoRotateSpeed = 0.8;

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
  const items = (skin.availableSkins ?? []) as Array<Record<string, any>>;
  const selectedSkin = items.find((item) => item.isSelected);
  const selectedFileName = String(selectedSkin?.fileName ?? skin.selectedSkinFileName ?? '');
  const selectedSkinUrl = typeof skin.selectedSkinUrl === 'string' && skin.selectedSkinUrl.trim().length > 0
    ? skin.selectedSkinUrl
    : null;
  const isSlim = !!skin.selectedSkinIsSlim;

  return (
    <>
      <PanelHeader title="Скин" subtitle={skin.selectedSkinLabel} />

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
            <label className="field-label">
              Файл
              <select className="launcher-select" value={selectedFileName} onChange={(event) => photinoBridge.sendCommand('skin.selectFile', { fileName: event.target.value })}>
                <option value="">PNG не выбран</option>
                {items.map((item) => <option key={String(item.fileName)} value={String(item.fileName)}>{item.fileName}</option>)}
              </select>
            </label>

            <p className="selected-file-copy">{skin.selectedSkinLabel || selectedFileName || 'Скин не выбран.'}</p>

            <div className="panel-actions-grid two-columns">
              <button className="subtle-button" onClick={() => photinoBridge.sendCommand('skin.importDialog')} type="button">Импорт</button>
              <button className="subtle-button" onClick={() => photinoBridge.sendCommand('skin.openFolder')} type="button">Папка</button>
              <button className="subtle-button" onClick={() => photinoBridge.sendCommand('skin.refresh')} type="button">Обновить</button>
              <button className="danger-button" onClick={() => photinoBridge.sendCommand('skin.clear')} type="button">Сбросить</button>
            </div>

            <SelectField label="Модель" value={skin.modelPreferenceId ?? 'auto'} options={(skin.modelOptions ?? []) as Array<Record<string, any>>} onChange={(value) => photinoBridge.sendCommand('skin.setModel', { modelId: value })} />
          </div>
        </div>
      </section>
    </>
  );
}
