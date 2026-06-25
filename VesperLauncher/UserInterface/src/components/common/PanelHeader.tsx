export function PanelHeader({ title, subtitle }: { title: string; subtitle?: string }) {
  return (
    <header className="panel-header">
      <p className="eyebrow">Раздел</p>
      <h2>{title}</h2>
      {subtitle && <p className="panel-subtitle">{subtitle}</p>}
    </header>
  );
}
