const fs = require('fs');

let css = fs.readFileSync('UserInterface/src/styles.css', 'utf8');

const rootReplacement = `:root {
  color-scheme: dark;
  --window-bg: #0a1118;
  --text: #f6faff;
  --text-muted: #c7daed;
  --text-dim: #9db2c4;
  
  --GlassButtonBackgroundBrush: linear-gradient(180deg, rgba(6,6,6,0.16) 0%, rgba(3,3,3,0.12) 52%, rgba(0,0,0,0.07) 100%);
  --GlassButtonHoverBrush: linear-gradient(180deg, rgba(16,16,16,0.20) 0%, rgba(12,12,12,0.15) 50%, rgba(6,6,6,0.09) 100%);
  --GlassButtonPressedBrush: linear-gradient(180deg, rgba(3,3,3,0.19) 0%, rgba(0,0,0,0.14) 52%, rgba(0,0,0,0.10) 100%);
  
  --GlassDangerButtonBackgroundBrush: linear-gradient(180deg, rgba(169,40,57,0.44) 0%, rgba(140,34,48,0.36) 52%, rgba(111,26,37,0.29) 100%);
  --GlassDangerButtonHoverBrush: linear-gradient(180deg, rgba(192,51,71,0.52) 0%, rgba(158,38,56,0.42) 50%, rgba(119,29,42,0.35) 100%);
  
  --GlassButtonBorderBrush: linear-gradient(180deg, rgba(255,255,255,0.23) 0%, rgba(255,255,255,0.07) 50%, rgba(255,255,255,0.01) 100%);
  --GlassButtonBorderHoverBrush: linear-gradient(180deg, rgba(255,255,255,0.29) 0%, rgba(255,255,255,0.09) 48%, rgba(255,255,255,0.02) 100%);
  
  --GlassButtonShineBrush: radial-gradient(190% 180% at 50% 0%, rgba(255,255,255,0.13) 0%, rgba(255,255,255,0.03) 35%, rgba(255,255,255,0) 100%);
  --GlassButtonLensBrush: linear-gradient(135deg, rgba(255,255,255,0.06) 0%, rgba(255,255,255,0.02) 22%, rgba(0,0,0,0.01) 52%, rgba(0,0,0,0.05) 82%, rgba(255,255,255,0.005) 100%);
  --GlassButtonRefractionBrush: radial-gradient(224% 164% at 38% 12%, rgba(143,214,255,0.06) 0%, rgba(255,255,255,0.015) 24%, rgba(0,0,0,0.05) 70%, transparent 100%);
  
  --GlassButtonInnerShadeBrush: linear-gradient(180deg, rgba(0,0,0,0.04) 0%, rgba(0,0,0,0.09) 52%, rgba(0,0,0,0.16) 100%);
  --GlassButtonTopEdgeBrush: linear-gradient(180deg, rgba(255,255,255,0.15) 0%, rgba(255,255,255,0.04) 42%, transparent 100%);
  --GlassButtonReflectionBrush: radial-gradient(190% 104% at 50% -8%, rgba(255,255,255,0.06) 0%, rgba(255,255,255,0.015) 24%, transparent 100%);
  --GlassButtonBottomRimBrush: linear-gradient(180deg, rgba(255,255,255,0.05) 0%, rgba(255,255,255,0.01) 55%, transparent 100%);
  
  --StableDayControlSurfaceBrush: rgba(0,0,0,0.024);
  --StableDayControlSurfaceHoverBrush: rgba(0,0,0,0.047);
  --StableDayPanelSurfaceBrush: rgba(0,0,0,0.06);
  
  --GlassPanelSurfaceWashBrush: linear-gradient(135deg, rgba(0,0,0,0.03) 0%, rgba(0,0,0,0.01) 46%, transparent 100%);
  --GlassPanelInnerEdgeBrush: linear-gradient(180deg, rgba(255,255,255,0.09) 0%, rgba(255,255,255,0.02) 28%, transparent 62%);
  --GlassPanelSoftLensBrush: linear-gradient(135deg, rgba(255,255,255,0.015) 0%, rgba(0,0,0,0.005) 55%, transparent 100%);
  
  --GlassButtonShadow: 0 10px 30px rgba(0,0,0,0.10);
  --GlassButtonShadowHover: 0 15px 40px rgba(0,0,0,0.14);
  --GlassButtonShadowPressed: 0 8px 24px rgba(0,0,0,0.08);
  
  font-family: "Segoe UI", "Segoe UI Variable", sans-serif;
}`;

css = css.replace(/:root\s*\{[\s\S]*?\}\s*(?=\* \{)/, rootReplacement + '\n\n');

css = css.replace(/\.classic-main-grid\s*\{[\s\S]*?\}/, `.classic-main-grid {
  width: 960px;
  height: 592px;
  position: relative;
  display: flex;
  margin: 0 auto;
}`);

css = css.replace(/\.left-control-surface\s*\{[\s\S]*?\}/, `.left-control-surface {
  position: absolute;
  left: 24px;
  top: 24px;
  width: 280px;
  bottom: 94px;
  border-radius: 19px;
  background: var(--StableDayPanelSurfaceBrush);
  border: 1px solid rgba(255,255,255,0.15);
  box-shadow: 0 15px 35px rgba(0,0,0,0.25);
  padding: 24px;
  display: flex;
  flex-direction: column;
  justify-content: space-between;
}`);

css = css.replace(/\.classic-side-panel\s*\{[\s\S]*?\}/, `.classic-side-panel {
  position: absolute;
  left: 320px;
  top: 24px;
  right: 24px;
  bottom: 94px;
  border-radius: 19px;
  background: var(--StableDayPanelSurfaceBrush);
  border: 1px solid rgba(255,255,255,0.15);
  box-shadow: 0 15px 35px rgba(0,0,0,0.25);
  padding: 24px;
  opacity: 0;
  pointer-events: none;
  transform: translateX(10px);
  transition: all 0.3s ease;
  overflow-y: auto;
  overflow-x: hidden;
}`);

css = css.replace(/\.bottom-action-bar\s*\{[\s\S]*?\}/, `.bottom-action-bar {
  position: absolute;
  left: 24px;
  right: 24px;
  bottom: 24px;
  height: 56px;
  border-radius: 16px;
  background: var(--StableDayPanelSurfaceBrush);
  border: 1px solid rgba(255,255,255,0.15);
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 10px;
  box-shadow: 0 15px 35px rgba(0,0,0,0.25);
}`);

const buttonCSS = `.main-glass-button, .static-glass-control, .input-icon-button {
  position: relative;
  background: var(--StableDayControlSurfaceBrush), var(--GlassButtonBackgroundBrush) !important;
  border-radius: 16px;
  box-shadow: var(--GlassButtonShadow) !important;
  color: #fff;
  font-weight: 600;
  font-size: 14px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: all 0.2s;
  border: 1px solid transparent !important;
}

.main-glass-button::before, .static-glass-control::before, .input-icon-button::before {
  content: '';
  position: absolute;
  inset: 0;
  border-radius: inherit;
  padding: 1px;
  background: var(--GlassButtonBorderBrush);
  -webkit-mask: linear-gradient(#fff 0 0) content-box, linear-gradient(#fff 0 0);
  -webkit-mask-composite: xor;
  mask-composite: exclude;
  pointer-events: none;
  z-index: 5;
}

.main-glass-button::after, .static-glass-control::after, .input-icon-button::after {
  content: '';
  position: absolute;
  inset: 1px;
  border-radius: calc(16px - 1px);
  background: 
    var(--GlassButtonReflectionBrush) 0 0 / 100% 100%,
    var(--GlassButtonInnerShadeBrush) 0 0 / 100% 100%,
    var(--GlassButtonRefractionBrush) 0 0 / 100% 100%,
    var(--GlassButtonLensBrush) 0 0 / 100% 100%,
    var(--GlassButtonShineBrush) 0 0 / 100% 100%;
  pointer-events: none;
  z-index: 4;
}

.main-glass-button:hover, .static-glass-control:hover, .input-icon-button:hover {
  background: var(--StableDayControlSurfaceHoverBrush), var(--GlassButtonHoverBrush) !important;
  box-shadow: var(--GlassButtonShadowHover) !important;
}
.main-glass-button:hover::before, .static-glass-control:hover::before, .input-icon-button:hover::before {
  background: var(--GlassButtonBorderHoverBrush);
}

.main-glass-button:active, .static-glass-control:active, .input-icon-button:active {
  background: var(--StableDayControlSurfaceBrush), var(--GlassButtonPressedBrush) !important;
  box-shadow: var(--GlassButtonShadowPressed) !important;
}

.main-glass-button.active {
  background: rgba(255,255,255,0.1), var(--GlassButtonHoverBrush) !important;
  box-shadow: 0 0 15px rgba(255,255,255,0.2) !important;
}`;

css = css + '\n\n' + buttonCSS + '\n';

fs.writeFileSync('UserInterface/src/styles.css', css, 'utf8');
console.log("CSS updated");

