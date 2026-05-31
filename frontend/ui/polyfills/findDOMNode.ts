// React 19 removed ReactDOM.findDOMNode, but semantic-ui-react 2.1.x still
// calls it via @fluentui/react-component-ref. Add a minimal fiber-walking
// shim so legacy wrappers keep working until semantic-ui-react ships a
// React 19-compatible release.
import * as ReactDOM from 'react-dom';

const reactDom = ReactDOM as unknown as Record<string, any>;
if (typeof reactDom.findDOMNode !== 'function') {
    reactDom.findDOMNode = (instance: any): Element | null => {
        if (instance == null) return null;
        if (instance instanceof Element) return instance;
        const fiber = instance._reactInternals || instance._reactInternalFiber;
        if (!fiber) return null;
        let node: any = fiber;
        while (node) {
            if (node.stateNode && node.stateNode instanceof Element) {
                return node.stateNode;
            }
            node = node.child;
        }
        return null;
    };
}

export {};
