export function SelectField({ label, value, options, onChange }: { label: string; value: string; options: Array<Record<string, any>>; onChange: (value: string) => void }) {
  return (
    <label className="field-label">
      {label}
      <select className="launcher-select" value={value} onChange={(event) => onChange(event.target.value)}>
        {options.map((option) => <option key={String(option.id)} value={String(option.id)}>{option.label}</option>)}
      </select>
    </label>
  );
}

export function InlineSelectField({ label, value, options, onChange }: { label: string; value: string; options: Array<Record<string, any>>; onChange: (value: string) => void }) {
  return (
    <label className="wpf-form-row inline-select-row">
      <span className="wpf-row-label">{label}</span>
      <select className="launcher-select" value={value} onChange={(event) => onChange(event.target.value)}>
        {options.map((option) => <option key={String(option.id)} value={String(option.id)}>{option.label}</option>)}
      </select>
    </label>
  );
}
