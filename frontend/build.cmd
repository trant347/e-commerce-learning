cd ./ui
call npm install
call npm run build
cd ..
docker build --build-arg version=0.0.1-SNAPSHOT . -t frontend