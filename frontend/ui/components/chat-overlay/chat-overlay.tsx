import * as React from 'react';
import { Link } from 'react-router-dom';
import Auth from '../../api/authenticationStorage';
import AiAssistantServices, { TaskMasterMention } from '../../api/aiAssistantServices';

import './chat-overlay.css';

/**
 * Splits `text` on task master names and replaces each occurrence with a
 * React <Link> pointing to /product/:id.
 */
function renderWithLinks(text: string, mentions: TaskMasterMention[], keyPrefix = 'mention'): React.ReactNode {
    if (!mentions.length) return text;

    // Sort longest name first to avoid partial matches on shorter names
    const sorted = [...mentions].sort((a, b) => b.name.length - a.name.length);

    // Split text into alternating plain/matched segments
    const parts: React.ReactNode[] = [];
    let remaining = text;

    while (remaining.length > 0) {
        let earliest: { index: number; mention: TaskMasterMention } | null = null;

        for (const mention of sorted) {
            const idx = remaining.indexOf(mention.name);
            if (idx !== -1 && (earliest === null || idx < earliest.index)) {
                earliest = { index: idx, mention };
            }
        }

        // if no matching mention found, push the rest of the text and break
        if (!earliest) {
            parts.push(remaining);
            break;
        }

        // push the part before the mention (if any), then the mention as a Link, and continue
        if (earliest.index > 0) {
            parts.push(remaining.slice(0, earliest.index));
        }
        parts.push(
            <Link key={`${keyPrefix}-${earliest.mention.id}-${parts.length}`} to={`/product/${earliest.mention.id}`}>
                {earliest.mention.name}
            </Link>
        );
        remaining = remaining.slice(earliest.index + earliest.mention.name.length);
    }

    return <>{parts}</>;
}

function toInternalProductPath(url: string): string | null {
    if (!url) return null;

    if (url.startsWith('/product/')) {
        return url;
    }

    try {
        const parsed = new URL(url);
        if (parsed.pathname.startsWith('/product/')) {
            return parsed.pathname;
        }
    } catch {
        return null;
    }

    return null;
}

function stripMarkdownImages(text: string): string {
    // Remove optional lead-in label plus markdown image token, e.g.
    // "Here's his profile picture: ![](https://...)"
    const withoutProfileLine = text.replace(
        /(?:^|\n)\s*Here(?:'|’)s\s+(?:his|her|their)\s+profile\s+picture:\s*!\[[^\]]*\]\([^\s)]+\)\s*(?=\n|$)/gi,
        ''
    );

    // Remove any remaining markdown image tokens.
    return withoutProfileLine
        .replace(/!\[[^\]]*\]\([^\s)]+\)/g, '')
        .replace(/\n{3,}/g, '\n\n')
        .trim();
}

function renderAssistantContent(text: string, mentions: TaskMasterMention[]): React.ReactNode {
    const sanitizedText = stripMarkdownImages(text);

    // Supports markdown-style links: [label](url)
    const linkRegex = /\[([^\]]+)\]\(([^)\s]+)\)/g;
    const nodes: React.ReactNode[] = [];
    let lastIndex = 0;
    let match: RegExpExecArray | null;
    let linkIndex = 0;

    while ((match = linkRegex.exec(sanitizedText)) !== null) {
        const before = sanitizedText.slice(lastIndex, match.index);
        if (before) {
            nodes.push(renderWithLinks(before, mentions, `plain-${linkIndex}`));
        }

        const label = match[1];
        const href = match[2];
        const internalPath = toInternalProductPath(href);

        if (internalPath) {
            nodes.push(
                <Link key={`md-internal-${linkIndex}`} to={internalPath}>
                    {label}
                </Link>
            );
        } else {
            nodes.push(
                <a key={`md-external-${linkIndex}`} href={href} target="_blank" rel="noreferrer noopener">
                    {label}
                </a>
            );
        }

        lastIndex = match.index + match[0].length;
        linkIndex += 1;
    }

    const tail = sanitizedText.slice(lastIndex);
    if (tail) {
        nodes.push(renderWithLinks(tail, mentions, `tail-${linkIndex}`));
    }

    return <>{nodes}</>;
}

interface IChatMessage {
    role: 'user' | 'assistant';
    content: string;
    mentions?: TaskMasterMention[];
}

interface IChatOverlayState {
    minimized: boolean;
    inputText: string;
    loading: boolean;
    messages: IChatMessage[];
}

export default class ChatOverlay extends React.Component<{}, IChatOverlayState> {
    constructor(props) {
        super(props);
        this.state = {
            minimized: false,
            inputText: '',
            loading: false,
            messages: [
                {
                    role: 'assistant',
                    content: 'Hi! I can help you find task masters for various services like plumbing, cleaning, tutoring, and more. I can also check your booking status.'
                }
            ]
        };
    }

    toggleMinimize() {
        this.setState({ minimized: !this.state.minimized });
    }

    onInputChange(event: React.ChangeEvent<HTMLInputElement>) {
        this.setState({ inputText: event.target.value });
    }

    onInputKeyDown(event: React.KeyboardEvent<HTMLInputElement>) {
        if (event.key === 'Enter') {
            this.sendMessage();
        }
    }

    async sendMessage() {
        const text = this.state.inputText.trim();
        if (!text || this.state.loading) {
            return;
        }

        const userMessage: IChatMessage = { role: 'user', content: text };
        const nextMessages = this.state.messages.concat(userMessage);

        this.setState({
            messages: nextMessages,
            inputText: '',
            loading: true
        });

        try {
            const userId = Auth.getUser();
            // Send only the immediately previous user+assistant exchange as history
            const prior = nextMessages.slice(1, -1); // exclude welcome greeting and current user message
            const history = prior.slice(-2).map(m => ({ role: m.role, content: m.content }));
            const response = await AiAssistantServices.chat(text, userId, history);
            const assistantMessage: IChatMessage = {
                role: 'assistant',
                content: response.answer || 'No answer returned from assistant.',
                mentions: response.mentions || []
            };
            this.setState({
                messages: nextMessages.concat(assistantMessage),
                loading: false
            });
        } catch (error) {
            this.setState({
                messages: nextMessages.concat({
                    role: 'assistant',
                    content: 'I could not reach the assistant service right now.'
                }),
                loading: false
            });
        }
    }

    render() {
        if (this.state.minimized) {
            return (
                <button className="chat-overlay-toggle" onClick={this.toggleMinimize.bind(this)}>
                    Chat
                </button>
            );
        }

        return (
            <div className="chat-overlay-container">
                <div className="chat-overlay-header">
                    <span>AI Assistant</span>
                    <button
                        className="chat-overlay-minimize-btn"
                        aria-label="Minimize chat"
                        onClick={this.toggleMinimize.bind(this)}
                    >
                        -
                    </button>
                </div>
                <div className="chat-overlay-body">
                    <div className="chat-overlay-messages">
                        {this.state.messages.map((message, index) => (
                            <div
                                key={index}
                                className={`chat-message-row ${message.role === 'user' ? 'from-user' : 'from-assistant'}`}
                            >
                                <div className="chat-message-bubble">
                                    {message.role === 'assistant'
                                        ? renderAssistantContent(message.content, message.mentions || [])
                                        : message.content}
                                </div>
                            </div>
                        ))}
                        {this.state.loading && (
                            <div className="chat-message-row from-assistant">
                                <div className="chat-message-bubble">Thinking...</div>
                            </div>
                        )}
                    </div>
                    <div className="chat-overlay-input-row">
                        <input
                            type="text"
                            className="chat-overlay-input"
                            placeholder="Type your question..."
                            value={this.state.inputText}
                            onChange={this.onInputChange.bind(this)}
                            onKeyDown={this.onInputKeyDown.bind(this)}
                            disabled={this.state.loading}
                        />
                        <button
                            className="chat-overlay-send-btn"
                            disabled={this.state.loading || !this.state.inputText.trim()}
                            onClick={this.sendMessage.bind(this)}
                        >
                            Send
                        </button>
                    </div>
                </div>
            </div>
        );
    }
}
