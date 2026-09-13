const {defineConfig,devices}=require('C:/Sean Project/RMT-1022-astra-implementation/product/client/node_modules/@playwright/test');
module.exports=defineConfig({testDir:'C:/Sean Project/RMT-1022-astra-implementation/product/client/tests',testMatch:['digital-thread-core-interaction.spec.ts','digital-thread-c5-acceptance.spec.ts'],workers:1,retries:0,expect:{timeout:15000},reporter:[['list'],['json',{outputFile:'C:/Users/seanm/AppData/Local/Temp/astra-1022-implementation-20260912/core-c5-6944761c.json'}]],outputDir:'C:/Users/seanm/AppData/Local/Temp/astra-1022-implementation-20260912/core-c5-6944761c-results',use:{...devices['Desktop Chrome'],baseURL:'http://127.0.0.1:5241',trace:'retain-on-failure',screenshot:'only-on-failure',video:'on'},webServer:{command:'npm run dev -- --host 127.0.0.1 --port 5241 --strictPort',cwd:'C:/Sean Project/RMT-1022-astra-implementation/product/client',url:'http://127.0.0.1:5241',reuseExistingServer:false}});





