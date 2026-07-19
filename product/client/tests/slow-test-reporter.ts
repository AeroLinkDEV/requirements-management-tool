import { mkdirSync, writeFileSync } from 'node:fs'
import type { FullResult, Reporter, TestCase, TestResult } from '@playwright/test/reporter'

type Timing={title:string;file:string;durationMs:number;status:string}

export default class SlowTestReporter implements Reporter{
  private timings:Timing[]=[]

  onTestEnd(test:TestCase,result:TestResult){
    this.timings.push({
      title:test.titlePath().filter(Boolean).join(' › '),
      file:test.location.file.replaceAll('\\','/'),
      durationMs:result.duration,
      status:result.status,
    })
  }

  onEnd(result:FullResult){
    const ordered=[...this.timings].sort((a,b)=>b.durationMs-a.durationMs)
    const outputDir=process.env.AEROLINK_E2E_OUTPUT_DIR??'test-results'
    mkdirSync(outputDir,{recursive:true})
    writeFileSync(`${outputDir}/test-timings.json`,JSON.stringify({status:result.status,tests:ordered},null,2))
    if(!ordered.length)return
    console.log('\nSlowest browser journeys:')
    for(const timing of ordered.slice(0,5))
      console.log(`  ${(timing.durationMs/1000).toFixed(1)}s  ${timing.title}`)
  }
}
