import axios from 'axios';

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

class AiAssistantServices {
    chat(message: string, userId?: string, history?: ChatHistoryMessage[]): Promise<ChatResponse> {
        return axios.post('/api/ai-assistant/chat', { message, userId, history }).then((res) => res.data);
    }
}

export default new AiAssistantServices();
