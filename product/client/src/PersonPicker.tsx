import { useEffect, useState } from 'react'
import './PersonPicker.css'

type Person={id:string;userName:string;displayName:string;email:string;title:string;roles:string[]}

/// What a chosen reviewer is: a person, and what they do here.
///
/// This showed the account handle under the name — `software.engineer.044` — which is how the database refers
/// to somebody, not how a colleague does. Somebody choosing approvers is deciding whether this person holds
/// the authority the decision needs, and a handle answers none of that. Their title and the roles they hold
/// in this Program do, so those are what stay on screen once the choice is made.
const describe=(person:Person)=>[person.title,...person.roles.filter(role=>role!==person.title)].filter(Boolean).join(' · ')

export default function PersonPicker({api,projectId,value,name,index,onSelect}:{api:string;projectId:string;value:string;name:string;index:number;onSelect:(person:{userId:string;name:string})=>void}){
 const [query,setQuery]=useState(name||value),[people,setPeople]=useState<Person[]>([]),[open,setOpen]=useState(false),[chosen,setChosen]=useState<Person>()
 // One letter is enough to start suggesting. Two meant typing "A" for Alex and being shown nothing at all,
 // which reads as "there is no such person" rather than "keep going". A staff directory is small. The command
 // palette still asks for two, because one letter there is a scan of every controlled record in the Project.
 useEffect(()=>{const timer=setTimeout(async()=>{if(query.trim().length<1){setPeople([]);return}const response=await fetch(`${api}/api/directory?projectId=${projectId}&search=${encodeURIComponent(query)}&limit=10`);if(response.ok)setPeople(await response.json())},150);return()=>clearTimeout(timer)},[api,projectId,query])
 return <div className="personPicker"><input aria-label={`Approver ${index+1} search`} value={query} placeholder="Search name, title, or role…" autoComplete="off" onFocus={()=>setOpen(true)} onChange={event=>{setQuery(event.target.value);setOpen(true);setChosen(undefined);onSelect({userId:'',name:''})}}/>{value&&chosen&&describe(chosen)&&<small>{describe(chosen)}</small>}{open&&people.length>0&&<div className="personSuggestions">{people.map(person=><button type="button" key={person.id} onClick={()=>{setChosen(person);onSelect({userId:person.userName,name:person.displayName});setQuery(person.displayName);setOpen(false)}}><i>{person.displayName.split(' ').map(x=>x[0]).join('').slice(0,2)}</i><span><b>{person.displayName}</b><small>{person.title}</small></span><em>{person.roles.join(' · ')}</em></button>)}</div>}</div>
}
