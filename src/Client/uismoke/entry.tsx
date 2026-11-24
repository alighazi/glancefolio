// Mount point for the jsdom smoke test. Kept separate from src/main.tsx so the
// test does not pull in the stylesheet import and can be bundled for node.
import { createRoot } from 'react-dom/client';
import App from '../src/App';

const el = document.getElementById('root');
if (!el) throw new Error('missing #root');

createRoot(el).render(<App />);
