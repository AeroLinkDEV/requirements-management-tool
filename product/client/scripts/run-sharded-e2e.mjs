import { spawn, spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const clientDir=fileURLToPath(new URL('..',import.meta.url))
const shardCount=Number.parseInt(process.env.AEROLINK_E2E_SHARDS??'3',10)
if(!Number.isInteger(shardCount)||shardCount<1)throw new Error('AEROLINK_E2E_SHARDS must be a positive integer.')

const dotnet=process.env.AEROLINK_DOTNET??'dotnet'
const playwrightCli=fileURLToPath(new URL('../node_modules/@playwright/test/cli.js',import.meta.url))
const startedAt=Date.now()

console.log('Preparing the browser-test API once...')
const build=spawnSync(dotnet,['build','../src/AeroLink.Api/AeroLink.Api.csproj','--configuration','Release'],{
  cwd:clientDir,
  stdio:'inherit',
})
if(build.status!==0)process.exit(build.status??1)

const runId=`sharded-${Date.now()}`
const runShard=(shard)=>new Promise((resolve)=>{
  const apiPort=Number.parseInt(process.env.AEROLINK_E2E_API_PORT_BASE??'5180',10)+shard
  const clientPort=Number.parseInt(process.env.AEROLINK_E2E_CLIENT_PORT_BASE??'5280',10)+shard
  const child=spawn(process.execPath,[playwrightCli,'test',`--shard=${shard}/${shardCount}`],{
    cwd:clientDir,
    env:{
      ...process.env,
      AEROLINK_E2E_SKIP_BUILD:'true',
      AEROLINK_E2E_RUN_ID:`${runId}-${shard}`,
      AEROLINK_E2E_API_PORT:String(apiPort),
      AEROLINK_E2E_CLIENT_PORT:String(clientPort),
      AEROLINK_E2E_OUTPUT_DIR:`test-results/shard-${shard}`,
      AEROLINK_E2E_REPORT_DIR:`playwright-report/shard-${shard}`,
    },
    stdio:'inherit',
  })
  child.on('exit',(code,signal)=>resolve({shard,code:code??1,signal}))
})

console.log(`Running ${shardCount} isolated browser shards...`)
const results=await Promise.all(Array.from({length:shardCount},(_,index)=>runShard(index+1)))
const failures=results.filter(result=>result.code!==0)
const elapsed=((Date.now()-startedAt)/1000).toFixed(1)
if(failures.length){
  console.error(`Browser shards failed: ${failures.map(result=>result.shard).join(', ')} (${elapsed}s).`)
  process.exit(1)
}
console.log(`All ${shardCount} browser shards passed in ${elapsed}s.`)
