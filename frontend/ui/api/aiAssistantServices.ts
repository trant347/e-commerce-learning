import axios from 'axios';
import Auth from './authenticationStorage';

export interface TaskMasterMention {
    id: string;
    name: string;
}

export interface ChatResponse {
    answer: string;
    model: string;
    sources: string[];
    mentions: TaskMasterMention[];
}

export interface ChatHistoryMessage {
    role: 'user' | 'assistant';
    content: string;
}

export interface AiAssistantConfig {
    historyMessageLimit: number;
}

class AiAssistantServices {
    getConfig(): Promise<AiAssistantConfig> {
        return axios
            .get('/api/ai-assistant/config')
            .then((res) => res.data);
    }

    chat(message: string, history?: ChatHistoryMessage[]): Promise<ChatResponse> {
        const token = Auth.getToken();
        const headers = token ? { Authorization: `Bearer ${token}` } : {};

        return axios
            .post('/api/ai-assistant/chat', { message, history }, { headers })
            .then((res) => res.data);
    }
}

export default new AiAssistantServices();
