import * as React from 'react';
import { useState, useEffect, useContext } from 'react';

import UserContext from './userContext';
import ApplicationBadgeContext from './applicationBadgeContext';
import { TaskMasterServices } from '../api/taskMasterServices';

export default function ApplicationBadgeProvider({ children }: { children: React.ReactNode }) {
    const { username } = useContext(UserContext);
    const [unviewedApplicationCount, setUnviewedApplicationCount] = useState<number>(0);

    // Fetch the initial count whenever the logged-in user changes
    useEffect(() => {
        if (username !== 'admin') {
            setUnviewedApplicationCount(0);
            return;
        }
        TaskMasterServices.getUnviewedCount()
            .then(count => setUnviewedApplicationCount(count))
            .catch(() => {});
    }, [username]);

    const decrementUnviewedCount = () => {
        setUnviewedApplicationCount(prev => Math.max(0, prev - 1));
    };

    return (
        <ApplicationBadgeContext.Provider value={{ unviewedApplicationCount, setUnviewedApplicationCount, decrementUnviewedCount }}>
            {children}
        </ApplicationBadgeContext.Provider>
    );
}
