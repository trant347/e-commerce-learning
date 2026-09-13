import './polyfills/findDOMNode.js';
import { createRoot } from 'react-dom/client';

import App from './containers/App';

const container = document.getElementById('container');
if (container) {
    createRoot(container).render(<App />);
}