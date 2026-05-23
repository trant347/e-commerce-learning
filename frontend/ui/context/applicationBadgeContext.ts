import * as React from 'react';

interface ApplicationBadgeContextType {
    unviewedApplicationCount: number;
    setUnviewedApplicationCount: (count: number) => void;
    decrementUnviewedCount: () => void;
}

export default React.createContext<ApplicationBadgeContextType>({
    unviewedApplicationCount: 0,
    setUnviewedApplicationCount: () => {},
    decrementUnviewedCount: () => {},
});
