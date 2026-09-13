const {defineConfig,devices}=require('C:/Sean Project/RMT-1022-astra-implementation/product/client/node_modules/@playwright/test');
module.exports=defineConfig({testDir:'C:/Sean Project/RMT-1022-astra-implementation/product/client/tests',testMatch:['digital-thread-core-interaction.spec.ts'],workers:1,retries:0,expect:{timeout:15000},reporter:[['list'],['json',{outputFile:'C:/Users/seanm/AppData/Local/Temp/astra-1022-implementation-20260912/core-a78e1bf7.json'}]],outputDir:'C:/Users/seanm/AppData/Local/Temp/astra-1022-implementation-20260912/core-a78e1bf7-results',use:{...devices['Desktop Chrome'],baseURL:'http://127.0.0.1:5234',trace:'retain-on-failure',screenshot:'only-on-failure',video:'on'},webServer:{command:'npm run dev -- --host 127.0.0.1 --port 5234 --strictPort',cwd:'C:/Sean Project/RMT-1022-astra-implementation/product/client',url:'http://127.0.0.1:5234',reuseExistingServer:false}});



