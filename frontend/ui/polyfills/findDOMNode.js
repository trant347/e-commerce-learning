// Plain JS so Jest can require it without ts-jest transformation.
// React 19 removed ReactDOM.findDOMNode, but semantic-ui-react 2.1.x still
// calls it via @fluentui/react-component-ref. Add a minimal fiber-walking
// shim so legacy wrappers keep working.
const ReactDOM = require('react-dom');

if (typeof ReactDOM.findDOMNode !== 'function') {
    ReactDOM.findDOMNode = function findDOMNode(instance) {
        if (instance == null) return null;
        if (typeof Element !== 'undefined' && instance instanceof Element) return instance;
        const fiber = instance._reactInternals || instance._reactInternalFiber;
        if (!fiber) return null;
        let node = fiber;
        while (node) {
            if (node.stateNode && typeof Element !== 'undefined' && node.stateNode instanceof Element) {
                return node.stateNode;
            }
            node = node.child;
        }
        return null;
    };
}

module.exports = {};
