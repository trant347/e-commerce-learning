import axios from 'axios';

export interface ChatResponse {
    answer: string;
    model: string;
    sources: string[];
}

class AiAssistantServices {
    chat(message: string, userId?: string): Promise<ChatResponse> {
        return axios.post('/api/ai-assistant/chat', { message, userId }).then((res) => res.data);
    }
}

export default new AiAssistantServices();
