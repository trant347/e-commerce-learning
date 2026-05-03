import * as React from 'react';
import Auth from '../../api/authenticationStorage';
import AiAssistantServices from '../../api/aiAssistantServices';

import './chat-overlay.css';

interface IChatMessage {
    role: 'user' | 'assistant';
    content: string;
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
                    content: 'Hi, I can help you find books and check booking information.'
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
            const response = await AiAssistantServices.chat(text, userId);
            const assistantMessage: IChatMessage = {
                role: 'assistant',
                content: response.answer || 'No answer returned from assistant.'
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
                                <div className="chat-message-bubble">{message.content}</div>
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
