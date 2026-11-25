import { useState, type FormEvent } from 'react';
import { api } from '../api';
import type { User } from '../types';
import { Icon, Spinner } from './ui';

export function AuthView({ onAuthenticated }: { onAuthenticated: (user: User) => void }) {
  const [mode, setMode] = useState<'login' | 'register'>('login');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const user = mode === 'login'
        ? await api.login(email.trim(), password)
        : await api.register(email.trim(), password);
      onAuthenticated(user);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not sign in. Please try again.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="auth-screen">
      <section className="auth-card">
        <div className="auth-mark"><Icon name="wallet" size={24} /></div>
        <div className="auth-brand">Glancefolio</div>
        <h1>{mode === 'login' ? 'Welcome back' : 'Create your account'}</h1>
        <p className="auth-copy">{mode === 'login'
          ? 'Sign in to see your portfolio, wherever you are.'
          : 'Your portfolio is private to your account.'}</p>
        <form className="stack" onSubmit={(e) => void submit(e)}>
          <label className="field">
            <span className="field__label">Email address</span>
            <input className="input" type="email" autoComplete="email" required maxLength={254}
              placeholder="you@example.com" value={email} onChange={(e) => setEmail(e.target.value)} />
          </label>
          <label className="field">
            <span className="field__label">Password</span>
            <input className="input" type="password" autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
              required minLength={mode === 'register' ? 12 : 1} maxLength={200}
              placeholder={mode === 'register' ? 'At least 12 characters' : 'Your password'}
              value={password} onChange={(e) => setPassword(e.target.value)} />
          </label>
          {error && <div className="notice notice--err" role="alert"><Icon name="alert" size={16} />{error}</div>}
          <button className="btn btn--primary btn--block" type="submit" disabled={busy}>
            {busy ? <Spinner /> : null}{busy ? 'Please wait…' : mode === 'login' ? 'Sign in' : 'Create account'}
          </button>
        </form>
        <div className="auth-toggle">
          {mode === 'login' ? 'New to Glancefolio?' : 'Already have an account?'}{' '}
          <button type="button" onClick={() => { setMode(mode === 'login' ? 'register' : 'login'); setError(null); }}>
            {mode === 'login' ? 'Create an account' : 'Sign in'}
          </button>
        </div>
        {mode === 'register' && <p className="auth-footnote">Use a unique password with at least 12 characters.</p>}
      </section>
    </main>
  );
}
