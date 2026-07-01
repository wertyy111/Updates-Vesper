import { photinoBridge } from '../../bridge';
import type { PanelRenderProps } from '../../types';
import { AvatarImage } from '../common/AvatarImage';

const notificationText = {
  title: '\u0423\u0432\u0435\u0434\u043e\u043c\u043b\u0435\u043d\u0438\u044f',
  subtitle: '\u0417\u0430\u044f\u0432\u043a\u0438 \u0432 \u0434\u0440\u0443\u0437\u044c\u044f \u0438 \u0432\u0430\u0436\u043d\u044b\u0435 \u0441\u043e\u0431\u044b\u0442\u0438\u044f Vesper.',
  friendRequests: '\u0417\u0430\u044f\u0432\u043a\u0438 \u0432 \u0434\u0440\u0443\u0437\u044c\u044f',
  empty: '\u041d\u043e\u0432\u044b\u0445 \u0443\u0432\u0435\u0434\u043e\u043c\u043b\u0435\u043d\u0438\u0439 \u043d\u0435\u0442.',
  accept: '\u041f\u0440\u0438\u043d\u044f\u0442\u044c',
  decline: '\u041e\u0442\u043a\u043b\u043e\u043d\u0438\u0442\u044c'
};

export function NotificationsPanel({ launcher }: PanelRenderProps) {
  const friends = launcher.friends;
  const incomingRequests = (friends.incomingRequests ?? []) as Array<Record<string, any>>;

  return (
    <div className="notifications-panel">
      <header className="panel-header">
        <div>
          <h2>{notificationText.title}</h2>
          <p className="panel-subtitle">{notificationText.subtitle}</p>
        </div>
      </header>

      <section className="notifications-section">
        <div className="notifications-section-head">
          <h3>{notificationText.friendRequests}</h3>
          {incomingRequests.length > 0 ? <span>{incomingRequests.length}</span> : null}
        </div>

        <div className="notifications-list">
          {incomingRequests.length === 0 ? (
            <p className="friends-empty-copy">{notificationText.empty}</p>
          ) : null}

          {incomingRequests.map((request) => (
            <article key={String(request.requestId)} className="notification-card">
              <div className="wpf-friend-avatar">
                <AvatarImage url={request.avatarUrl} placeholder={request.avatarPlaceholder} alt={String(request.username)} />
              </div>

              <div className="wpf-friend-info">
                <strong>{request.username}</strong>
                <p>{request.subtitleText}</p>
              </div>

              <div className="wpf-friend-actions">
                <button
                  className="subtle-button compact"
                  onClick={() => photinoBridge.sendCommand('friends.respond', { requestId: request.requestId, action: 'accept' })}
                  type="button"
                >
                  {notificationText.accept}
                </button>
                <button
                  className="danger-button compact"
                  onClick={() => photinoBridge.sendCommand('friends.respond', { requestId: request.requestId, action: 'decline' })}
                  type="button"
                >
                  {notificationText.decline}
                </button>
              </div>
            </article>
          ))}
        </div>
      </section>
    </div>
  );
}
