import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './styles.css';

window.addEventListener('error', (event) => {
  try {
    const errorMsg = event.error ? event.error.stack || event.error.message : event.message;
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/bridge-message', false);
    xhr.setRequestHeader('Content-Type', 'application/json');
    xhr.send(JSON.stringify({
      type: 'command',
      command: 'host.logJsError',
      payload: { error: `Global Error: ${errorMsg} at ${event.filename}:${event.lineno}:${event.colno}` }
    }));
  } catch (e) {}
});

window.addEventListener('unhandledrejection', (event) => {
  try {
    const reason = event.reason;
    const errorMsg = reason ? (reason.stack || reason.message || reason) : 'Unknown promise rejection';
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/bridge-message', false);
    xhr.setRequestHeader('Content-Type', 'application/json');
    xhr.send(JSON.stringify({
      type: 'command',
      command: 'host.logJsError',
      payload: { error: `Unhandled Rejection: ${errorMsg}` }
    }));
  } catch (e) {}
});

const originalConsoleError = console.error;
console.error = (...args: any[]) => {
  originalConsoleError.apply(console, args);
  try {
    const msg = args.map(arg => typeof arg === 'object' ? JSON.stringify(arg) : String(arg)).join(' ');
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/bridge-message', false);
    xhr.setRequestHeader('Content-Type', 'application/json');
    xhr.send(JSON.stringify({
      type: 'command',
      command: 'host.logJsError',
      payload: { error: `Console.error: ${msg}` }
    }));
  } catch (e) {}
};

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
