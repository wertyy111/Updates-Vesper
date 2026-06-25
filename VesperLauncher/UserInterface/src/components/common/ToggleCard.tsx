export function ToggleCard({ label, hint, checked, onChange }: { label: string; hint?: string; checked: boolean; onChange: (value: boolean) => void }) {
  return (
    <div className="toggle-card glass-inset">
      <div>
        <strong>{label}</strong>
        <p>{hint || '—'}</p>
      </div>
      <div className={`settings-segmented-toggle toggle-card-segmented left-liquid-glass-button settings-liquid-glass-button ${checked ? 'is-left-active' : 'is-right-active'}`} role="group" aria-label={label}>
        <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
        <button className={checked ? 'active' : ''} onClick={() => onChange(true)} type="button">Вкл</button>
        <button className={!checked ? 'active' : ''} onClick={() => onChange(false)} type="button">Выкл</button>
      </div>
    </div>
  );
}
