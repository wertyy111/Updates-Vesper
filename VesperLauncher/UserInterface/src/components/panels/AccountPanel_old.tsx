import { photinoBridge } from '../../bridge';
import type { PanelRenderProps } from '../../types';
import { AvatarImage } from '../common/AvatarImage';
import { PanelHeader } from '../common/PanelHeader';

const accountText = {
  title: '\u0410\u043a\u043a\u0430\u0443\u043d\u0442 Vesper',
  noAccount: '\u0411\u0435\u0437 \u0430\u043a\u043a\u0430\u0443\u043d\u0442\u0430',
  cloudActive: '\u041e\u0431\u043b\u0430\u0447\u043d\u0430\u044f \u0441\u0435\u0441\u0441\u0438\u044f \u0430\u043a\u0442\u0438\u0432\u043d\u0430.',
  sessionInactive: '\u0421\u0435\u0441\u0441\u0438\u044f \u043d\u0435 \u0430\u043a\u0442\u0438\u0432\u043d\u0430.',
  changeAvatar: '\u0421\u043c\u0435\u043d\u0438\u0442\u044c \u0430\u0432\u0430\u0442\u0430\u0440',
  editProfile: '\u0418\u0437\u043c\u0435\u043d\u0438\u0442\u044c \u043f\u0440\u043e\u0444\u0438\u043b\u044c',
  logout: '\u0412\u044b\u0439\u0442\u0438',
  login: '\u0412\u0445\u043e\u0434',
  register: '\u0420\u0435\u0433\u0438\u0441\u0442\u0440\u0430\u0446\u0438\u044f',
  nickname: '\u041d\u0438\u043a',
  enterNickname: '\u0412\u0432\u0435\u0434\u0438\u0442\u0435 \u043d\u0438\u043a',
  password: '\u041f\u0430\u0440\u043e\u043b\u044c',
  enterPassword: '\u0412\u0432\u0435\u0434\u0438\u0442\u0435 \u043f\u0430\u0440\u043e\u043b\u044c',
  createAccount: '\u0421\u043e\u0437\u0434\u0430\u0442\u044c \u0430\u043a\u043a\u0430\u0443\u043d\u0442',
  signIn: '\u0412\u043e\u0439\u0442\u0438',
};

export function AccountPanel({ launcher, accountForm, setAccountForm, setAccountDirty, submitAccount }: PanelRenderProps) {
  const account = launcher.account;
  const recentUsernames = (account.recentUsernames ?? []) as string[];
  const modes = ['login', 'register'] as const;
  const nickname = String(account.currentNickname || '').trim();

  return (
    <>
      <PanelHeader title={accountText.title} subtitle={account.accountStateText} />

      <div className="avatar-summary account-avatar-summary">
        <AvatarImage
          className="avatar-preview"
          url={account.avatarUrl}
          placeholder={account.avatarPlaceholder}
          alt={nickname || 'avatar'}
        />
        <div>
          <h3>{nickname || accountText.noAccount}</h3>
          <p>{account.hasAuthenticatedSession ? accountText.cloudActive : accountText.sessionInactive}</p>
        </div>
      </div>

      {account.mode === 'summary' ? (
        <div className="stack-layout account-panel-v2">
          <button className="primary-button" onClick={() => photinoBridge.sendCommand('account.pickAvatar')} type="button">
            {account.canChangeAvatar ? accountText.changeAvatar : accountText.editProfile}
          </button>
          <button className="danger-button" onClick={() => photinoBridge.sendCommand('account.logout')} type="button">
            {accountText.logout}
          </button>
          <div className="chip-group">
            {recentUsernames.map((username) => (
              <button
                key={username}
                className="chip-button"
                onClick={() => photinoBridge.sendCommand('account.selectRecentUsername', { username })}
                type="button"
              >
                {username}
              </button>
            ))}
          </div>
        </div>
      ) : (
        <div className="stack-layout account-panel-v2">
          <div className={`account-segmented-toggle left-liquid-glass-button settings-liquid-glass-button ${accountForm.mode === 'login' ? 'is-left-active' : 'is-right-active'}`} role="group">
            <span className="settings-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
            <button className={accountForm.mode === 'login' ? 'active left-liquid-glass-content' : 'left-liquid-glass-content'} onClick={() => {
              setAccountDirty(false);
              setAccountForm((previous: any) => ({ ...previous, mode: 'login', password: '' }));
              photinoBridge.sendCommand('account.setMode', { mode: 'login' });
            }} type="button">{accountText.login}</button>
            <button className={accountForm.mode === 'register' ? 'active left-liquid-glass-content' : 'left-liquid-glass-content'} onClick={() => {
              setAccountDirty(false);
              setAccountForm((previous: any) => ({ ...previous, mode: 'register', password: '' }));
              photinoBridge.sendCommand('account.setMode', { mode: 'register' });
            }} type="button">{accountText.register}</button>
          </div>

          <label className="field-label">
            {accountText.nickname}
            <input
              className="launcher-input"
              value={accountForm.username}
              onChange={(event) => {
                setAccountDirty(true);
                setAccountForm((previous: any) => ({ ...previous, username: event.target.value }));
              }}
              placeholder={accountText.enterNickname}
            />
          </label>

          <label className="field-label">
            {accountText.password}
            <input
              className="launcher-input"
              type="password"
              value={accountForm.password}
              onChange={(event) => {
                setAccountDirty(true);
                setAccountForm((previous: any) => ({ ...previous, password: event.target.value }));
              }}
              placeholder={accountText.enterPassword}
            />
          </label>

          {(() => {
            const label = accountForm.mode === 'register' ? accountText.createAccount : accountText.signIn;
            return (
              <button
                className="primary-button left-liquid-glass-button"
                data-liquid-label={label}
                onClick={submitAccount}
                type="button"
              >
                <span className="left-liquid-glass-layer liquid-glass-layer" aria-hidden="true" />
                <span className="left-liquid-glass-content">{label}</span>
              </button>
            );
          })()}

          <div className="chip-group">
            {recentUsernames.map((username) => (
              <button
                key={username}
                className="chip-button"
                onClick={() => {
                  setAccountDirty(true);
                  setAccountForm((previous: any) => ({ ...previous, username }));
                }}
                type="button"
              >
                {username}
              </button>
            ))}
          </div>
        </div>
      )}
    </>
  );
}

