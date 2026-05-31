/** @type {import('jest').Config} */
module.exports = {
    testEnvironment: 'jsdom',
    roots: ['<rootDir>'],
    testMatch: ['<rootDir>/**/*.(test|spec).(ts|tsx)'],
    moduleNameMapper: {
        '\\.(css|less|scss|sass)$': 'identity-obj-proxy',
        '\\.(png|jpg|jpeg|gif|svg)$': '<rootDir>/__mocks__/fileMock.js'
    },
    transform: {
        '^.+\\.(ts|tsx)$': ['ts-jest', {
            tsconfig: {
                jsx: 'react',
                esModuleInterop: true,
                target: 'es2017',
                module: 'commonjs',
                lib: ['es2017', 'dom'],
                allowSyntheticDefaultImports: true,
                skipLibCheck: true,
                isolatedModules: true
            }
        }]
    }
};
