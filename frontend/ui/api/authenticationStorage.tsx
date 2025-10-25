export const UserNameKey = "USER_NAME_KEY_BOOKSTORE";
class Auth {

    /**
     * Authenticate a user. Save a token string in Local Storage
     *
     * @param {string} token
     */
    static authenticateUser(token: string, username?: string) {
        localStorage.setItem('token', token);
        localStorage.setItem(UserNameKey, username);
    }

    /**
     * Check if a user is authenticated - check if a token is saved in Local Storage
     *
     * @returns {boolean}
     */
    static isUserAuthenticated() {
        return localStorage.getItem('token') !== null;
    }

    /**
     * Deauthenticate a user. Remove a token from Local Storage.
     *
     */
    static deauthenticateUser() {
        localStorage.removeItem('token');
        localStorage.removeItem(UserNameKey);
    }

    /**
     * Get a token value.
     *
     * @returns {string}
     */

    static getToken() {
        return localStorage.getItem('token');
    }

    static getUser() : string {
        return localStorage.getItem(UserNameKey) || null;
    }


}

export default Auth;