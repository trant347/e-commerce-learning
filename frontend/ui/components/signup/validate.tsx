export function validateEmail(value: string) : string {
    let error = null;
    if (!value) {
      error = 'Email is required';
    } else if (!/^[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,4}$/i.test(value)) {
      error = 'Invalid email address';
    }
    return error;
}
  
export function validatePassword(value: string) : string {
    let error = null;
    if (value === 'password') {
        error = 'Nice try!';
    }
    return error;
}