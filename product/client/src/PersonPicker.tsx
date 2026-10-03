import { useEffect, useState } from 'react'
import { useLatestRequest } from './useLatestRequest'
import './PersonPicker.css'

type Person={id:string;userName:string;displayName:string;email:string;title:string;roles:string[]}

/// What a chosen reviewer is: a person, and what they do here.
/// Account handles are database references, not how a colleague identifies a reviewer; the UI keeps the
/// person’s title and Program authority visible instead.
const describe=(person:Person)=>[person.title,...person.roles.filter(role=>role!==person.title)].filter(Boolean).join(' · ')

export default function PersonPicker({api,projectId,value,name,index,label,excludeUserNames=[],allowedRoles,authority,onSelect}:{
 api:string;projectId:string;value:string;name:string;index:number;label?:string;excludeUserNames?:string[];
 allowedRoles?:string[];authority?:string;
 onSelect:(person:{userId:string;name:string})=>void
}){
 const [query,setQuery]=useState(name||value),[people,setPeople]=useState<Person[]>([]),[open,setOpen]=useState(false),[chosen,setChosen]=useState<Person>()
 const {begin,invalidate}=useLatestRequest()
 const rolesQuery=allowedRoles?.length?allowedRoles.join(","):undefined
 const excluded=new Set(excludeUserNames.map(userName=>userName.toLowerCase()))
 const allowed=allowedRoles?new Set(allowedRoles.map(role=>role.toLowerCase())):null
 const available=people.filter(person=>!excluded.has(person.userName.toLowerCase())&&(!allowed||person.roles.some(role=>allowed.has(role.toLowerCase()))))
 useEffect(()=>{
  // Own the intent now, including the debounce interval before its request starts.
  const isCurrent=begin()
  setPeople([])
  if(!query.trim())return invalidate
  const timer=setTimeout(async()=>{
   const scope=(authority?`&authority=${encodeURIComponent(authority)}`:"")+(rolesQuery!==undefined?`&roles=${encodeURIComponent(rolesQuery)}`:"")
   const response=await fetch(`${api}/api/directory?projectId=${projectId}&search=${encodeURIComponent(query)}&limit=10${scope}`)
   if(!isCurrent()||!response.ok)return
   const results:Person[]=await response.json()
   if(isCurrent())setPeople(results)
  },150)
  return()=>{clearTimeout(timer);invalidate()}
 },[api,projectId,query,authority,rolesQuery,begin,invalidate])
 return <div className="personPicker"><input aria-label={label??`Approver ${index+1} search`} value={query} placeholder="Search name, title, or role…" autoComplete="off" onFocus={()=>setOpen(true)} onChange={event=>{invalidate();setPeople([]);setQuery(event.target.value);setOpen(true);setChosen(undefined);onSelect({userId:'',name:''})}}/>{value&&chosen&&describe(chosen)&&<small>{describe(chosen)}</small>}{open&&available.length>0&&<div className="personSuggestions">{available.map(person=><button type="button" key={person.id} data-user-name={person.userName} onClick={()=>{invalidate();setPeople([]);setChosen(person);onSelect({userId:person.userName,name:person.displayName});setQuery(person.displayName);setOpen(false)}}><i>{person.displayName.split(' ').map(x=>x[0]).join('').slice(0,2)}</i><span><b>{person.displayName}</b><small>{person.title}</small></span><em>{person.roles.join(' · ')}</em></button>)}</div>}</div>
}
